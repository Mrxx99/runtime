using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

using static Microsoft.Interop.Analyzers.AnalyzerDiagnostics;

namespace Microsoft.Interop.Analyzers
{
    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public sealed class ConvertToGeneratedDllImportFixer : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Ids.ConvertToGeneratedDllImport);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public const string NoPreprocessorDefinesKey = "ConvertToGeneratedDllImport";
        public const string WithPreprocessorDefinesKey = "ConvertToGeneratedDllImportPreprocessor";

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            // Get the syntax root and semantic model
            Document doc = context.Document;
            SyntaxNode? root = await doc.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SemanticModel? model = await doc.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || model == null)
                return;

            // Nothing to do if the GeneratedDllImportAttribute is not in the compilation
            INamedTypeSymbol? generatedDllImportAttrType = model.Compilation.GetTypeByMetadataName(TypeNames.GeneratedDllImportAttribute);
            if (generatedDllImportAttrType == null)
                return;

            INamedTypeSymbol? dllImportAttrType = model.Compilation.GetTypeByMetadataName(typeof(DllImportAttribute).FullName);
            if (dllImportAttrType == null)
                return;

            // Get the syntax node tied to the diagnostic and check that it is a method declaration
            if (root.FindNode(context.Span) is not MethodDeclarationSyntax methodSyntax)
                return;

            if (model.GetDeclaredSymbol(methodSyntax, context.CancellationToken) is not IMethodSymbol methodSymbol)
                return;

            // Make sure the method has the DllImportAttribute
            AttributeData? dllImportAttr;
            if (!TryGetAttribute(methodSymbol, dllImportAttrType, out dllImportAttr))
                return;

            // Register code fixes with two options for the fix - using preprocessor or not.
            context.RegisterCodeFix(
                CodeAction.Create(
                    Resources.ConvertToGeneratedDllImportNoPreprocessor,
                    cancelToken => ConvertToGeneratedDllImport(
                        context.Document,
                        methodSyntax,
                        methodSymbol,
                        dllImportAttr!,
                        generatedDllImportAttrType,
                        usePreprocessorDefines: false,
                        cancelToken),
                    equivalenceKey: NoPreprocessorDefinesKey),
                context.Diagnostics);

            context.RegisterCodeFix(
                CodeAction.Create(
                    Resources.ConvertToGeneratedDllImportWithPreprocessor,
                    cancelToken => ConvertToGeneratedDllImport(
                        context.Document,
                        methodSyntax,
                        methodSymbol,
                        dllImportAttr!,
                        generatedDllImportAttrType,
                        usePreprocessorDefines: true,
                        cancelToken),
                    equivalenceKey: WithPreprocessorDefinesKey),
                context.Diagnostics);
        }

        private async Task<Document> ConvertToGeneratedDllImport(
            Document doc,
            MethodDeclarationSyntax methodSyntax,
            IMethodSymbol methodSymbol,
            AttributeData dllImportAttr,
            INamedTypeSymbol generatedDllImportAttrType,
            bool usePreprocessorDefines,
            CancellationToken cancellationToken)
        {
            DocumentEditor editor = await DocumentEditor.CreateAsync(doc, cancellationToken).ConfigureAwait(false);
            SyntaxGenerator generator = editor.Generator;

            var dllImportSyntax = (AttributeSyntax)dllImportAttr!.ApplicationSyntaxReference!.GetSyntax(cancellationToken);

            // Create GeneratedDllImport attribute based on the DllImport attribute
            var generatedDllImportSyntax = GetGeneratedDllImportAttribute(
                generator,
                dllImportSyntax,
                methodSymbol.GetDllImportData()!,
                generatedDllImportAttrType);

            // Add annotation about potential behavioural and compatibility changes
            generatedDllImportSyntax = generatedDllImportSyntax.WithAdditionalAnnotations(
                WarningAnnotation.Create(string.Format(Resources.ConvertToGeneratedDllImportWarning, "[TODO] Documentation link")));

            // Replace DllImport with GeneratedDllImport
            SyntaxNode generatedDeclaration = generator.ReplaceNode(methodSyntax, dllImportSyntax, generatedDllImportSyntax);

            // Replace extern keyword with partial keyword
            generatedDeclaration = generator.WithModifiers(
                generatedDeclaration,
                generator.GetModifiers(methodSyntax)
                    .WithIsExtern(false)
                    .WithPartial(true));

            if (!usePreprocessorDefines)
            {
                // Replace the original method with the updated one
                editor.ReplaceNode(methodSyntax, generatedDeclaration);
            }
            else
            {
                // #if NET
                generatedDeclaration = generatedDeclaration.WithLeadingTrivia(
                    generatedDeclaration.GetLeadingTrivia()
                        .AddRange(new[] {
                            SyntaxFactory.Trivia(SyntaxFactory.IfDirectiveTrivia(SyntaxFactory.IdentifierName("NET"), isActive: true, branchTaken: true, conditionValue: true)),
                            SyntaxFactory.ElasticMarker
                        }));

                // #else
                generatedDeclaration = generatedDeclaration.WithTrailingTrivia(
                    generatedDeclaration.GetTrailingTrivia()
                        .AddRange(new[] {
                            SyntaxFactory.Trivia(SyntaxFactory.ElseDirectiveTrivia(isActive: false, branchTaken: false)),
                            SyntaxFactory.ElasticMarker
                        }));

                // Remove existing leading trivia - it will be on the GeneratedDllImport method
                var updatedDeclaration = methodSyntax.WithLeadingTrivia();

                // #endif
                updatedDeclaration = updatedDeclaration.WithTrailingTrivia(
                    methodSyntax.GetTrailingTrivia()
                        .AddRange(new[] {
                            SyntaxFactory.Trivia(SyntaxFactory.EndIfDirectiveTrivia(isActive: true)),
                            SyntaxFactory.ElasticMarker
                        }));

                // Add the GeneratedDllImport method
                editor.InsertBefore(methodSyntax, generatedDeclaration);

                // Replace the original method with the updated DllImport method
                editor.ReplaceNode(methodSyntax, updatedDeclaration);
            }

            return editor.GetChangedDocument();
        }

        private SyntaxNode GetGeneratedDllImportAttribute(
            SyntaxGenerator generator,
            AttributeSyntax dllImportSyntax,
            DllImportData dllImportData,
            INamedTypeSymbol generatedDllImportAttrType)
        {
            // Create GeneratedDllImport based on the DllImport attribute
            var generatedDllImportSyntax = generator.ReplaceNode(dllImportSyntax,
                dllImportSyntax.Name,
                generator.TypeExpression(generatedDllImportAttrType));

            // Update attribute arguments for GeneratedDllImport
            List<SyntaxNode> argumentsToRemove = new List<SyntaxNode>();
            foreach (SyntaxNode argument in generator.GetAttributeArguments(generatedDllImportSyntax))
            {
                if (argument is not AttributeArgumentSyntax attrArg)
                    continue;

                if (dllImportData.BestFitMapping != null
                    && !dllImportData.BestFitMapping.Value
                    && IsMatchingNamedArg(attrArg, nameof(DllImportAttribute.BestFitMapping)))
                {
                    // BestFitMapping=false is explicitly set
                    // GeneratedDllImport does not support setting BestFitMapping. The generated code
                    // has the equivalent behaviour of BestFitMapping=false, so we can remove the argument.
                    argumentsToRemove.Add(argument);
                }
                else if (dllImportData.ThrowOnUnmappableCharacter != null
                    && !dllImportData.ThrowOnUnmappableCharacter.Value
                    && IsMatchingNamedArg(attrArg, nameof(DllImportAttribute.ThrowOnUnmappableChar)))
                {
                    // ThrowOnUnmappableChar=false is explicitly set
                    // GeneratedDllImport does not support setting ThrowOnUnmappableChar. The generated code
                    // has the equivalent behaviour of ThrowOnUnmappableChar=false, so we can remove the argument.
                    argumentsToRemove.Add(argument);
                }
            }

            return generator.RemoveNodes(generatedDllImportSyntax, argumentsToRemove);
        }

        private static bool TryGetAttribute(IMethodSymbol method, INamedTypeSymbol attributeType, out AttributeData? attr)
        {
            attr = default;
            foreach (var attrLocal in method.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attrLocal.AttributeClass, attributeType))
                {
                    attr = attrLocal;
                    return true;
                }
            }

            return false;
        }

        private static bool IsMatchingNamedArg(AttributeArgumentSyntax arg, string nameToMatch)
        {
            return arg.NameEquals != null && arg.NameEquals.Name.Identifier.Text == nameToMatch;
        }
    }
}
