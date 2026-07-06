// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;
using Microsoft.TypeSpec.Generator.Tests.Providers.NamedTypeSymbolProviders;
using Microsoft.TypeSpec.Generator.Tests.TestHelpers;
using NUnit.Framework;

namespace Microsoft.TypeSpec.Generator.Tests.ReferenceMap
{
    public class ProviderReferenceMapAnalyzerTests
    {
        [SetUp]
        public void SetUp()
        {
            ProviderReferenceMapAnalyzer.ResetPreWriteAccessibility();
        }

        [TearDown]
        public void TearDown()
        {
            ProviderReferenceMapAnalyzer.ResetPreWriteAccessibility();
        }

        [Test]
        public void NonRootKeptTypesKeepTheirAccessibility()
        {
            var context = new TestTypeProvider("SampleContext", TypeSignatureModifiers.Public);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(context));
            CodeModelGenerator.Instance.AddTypeToKeep(context.Type.FullyQualifiedName, isRoot: false);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([context]);

            Assert.IsTrue(context.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsFalse(context.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
        }

        [Test]
        public void NonRootKeptTypesAreWrittenWithoutRootingOtherTypes()
        {
            var context = new TestTypeProvider("SampleContext", TypeSignatureModifiers.Public);
            var unusedModel = new TestTypeProvider("UnusedModel", TypeSignatureModifiers.Public);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(context, unusedModel));
            CodeModelGenerator.Instance.AddTypeToKeep(context.Type.FullyQualifiedName, isRoot: false);

            ProviderReferenceMapAnalyzer.Analyze([context, unusedModel]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(context));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(unusedModel));
        }

        [Test]
        public void AmbiguousNamespaceLessCustomCodeBodyDependencyDoesNotRootGeneratedTypeBySimpleName()
        {
            var customCodeView = new BodyDependencyTestTypeProvider("CustomType", CreateNamedType("Error", string.Empty));
            var customType = new CustomizableTestTypeProvider("CustomType", TypeSignatureModifiers.Public, customCodeView, ns: "Sample");
            var generatedError = new TestTypeProvider("Error", TypeSignatureModifiers.Public, ns: "Sample.Models");
            var otherGeneratedError = new TestTypeProvider("Error", TypeSignatureModifiers.Public, ns: "Other.Models");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, generatedError, otherGeneratedError));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([customType, generatedError, otherGeneratedError]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(customType));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(generatedError));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(otherGeneratedError));
        }

        [Test]
        public void NamespaceLessCustomCodeBodyDependencyRootsGeneratedTypeInCustomCodeNamespace()
        {
            var customCodeView = new BodyDependencyTestTypeProvider("CustomType", "Sample", CreateNamedType("ReferencedModel", string.Empty));
            var customType = new CustomizableTestTypeProvider("CustomType", TypeSignatureModifiers.Public, customCodeView, ns: "Sample");
            var referencedModel = new TestTypeProvider("ReferencedModel", TypeSignatureModifiers.Public, ns: "Sample");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, referencedModel));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([customType, referencedModel]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(customType));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(referencedModel));
        }

        [Test]
        public void NamespaceLessCustomCodeGenericBodyDependencyRootsGeneratedTypeInCustomCodeNamespace()
        {
            var genericArgument = CreateNamedType("T", string.Empty);
            var customCodeView = new BodyDependencyTestTypeProvider("CustomType", "Sample", CreateNamedType("ErrorResult", string.Empty, genericArgument));
            var customType = new CustomizableTestTypeProvider("CustomType", TypeSignatureModifiers.Public, customCodeView, ns: "Sample");
            var errorResult = new GenericTestTypeProvider("ErrorResult", TypeSignatureModifiers.Internal, "Sample", genericArgument);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, errorResult));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([customType, errorResult]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(customType));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(errorResult));
        }

        [Test]
        public void NamespaceLessGeneratedGenericBodyDependencyRootsGeneratedTypeInProviderNamespace()
        {
            var genericArgument = CreateNamedType("T", string.Empty);
            var provider = new BodyDependencyTestTypeProvider("ClientPipelineExtensions", "Sample", CreateNamedType("ErrorResult", "Microsoft.TypeSpec.Generator.ClientModel.Providers", genericArgument));
            var errorResult = new GenericTestTypeProvider("ErrorResult", TypeSignatureModifiers.Internal, "Sample", genericArgument);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(provider, errorResult));
            CodeModelGenerator.Instance.AddTypeToKeep(provider.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([provider, errorResult]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(provider));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(errorResult));
        }

        [Test]
        public void HelperRootBodyDependencyRootsGeneratedGenericDependency()
        {
            var genericArgument = CreateNamedType("T", string.Empty);
            var client = new HelperDependencyTestTypeProvider("SampleClient", "Sample", CreateNamedType("ClientPipelineExtensions", "Microsoft.TypeSpec.Generator.ClientModel.Providers"));
            var pipelineExtensions = new BodyDependencyTestTypeProvider("ClientPipelineExtensions", "Sample", CreateNamedType("ErrorResult", "Microsoft.TypeSpec.Generator.ClientModel.Providers", genericArgument));
            var errorResult = new GenericTestTypeProvider("ErrorResult", TypeSignatureModifiers.Internal, "Sample", genericArgument);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(client, pipelineExtensions, errorResult));
            CodeModelGenerator.Instance.AddTypeToKeep(client.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([client, pipelineExtensions, errorResult]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(client));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(pipelineExtensions));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(errorResult));
        }

        [Test]
        public void UnreferencedHelperDependencyDoesNotKeepUnreferencedSiblingHelper()
        {
            var client = new HelperDependencyTestTypeProvider("SampleClient", "Sample", CreateNamedType("ClientPipelineExtensions", "Sample"));
            var pipelineExtensions = new TestTypeProvider("ClientPipelineExtensions", TypeSignatureModifiers.Internal | TypeSignatureModifiers.Static, ns: "Sample");
            var cancellationTokenExtensions = new TestTypeProvider("CancellationTokenExtensions", TypeSignatureModifiers.Internal | TypeSignatureModifiers.Static, ns: "Sample");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(client, pipelineExtensions, cancellationTokenExtensions));
            CodeModelGenerator.Instance.AddTypeToKeep(client.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([client, pipelineExtensions, cancellationTokenExtensions]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(client));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(pipelineExtensions));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(cancellationTokenExtensions));
        }

        [Test]
        public async Task LastContractInternalHelperDoesNotRootAbsentGeneratedHelper()
        {
            var lastContractCompilation = CSharpCompilation.Create(
                "LastContract",
                [CSharpSyntaxTree.ParseText("""
                    namespace Sample
                    {
                        internal static class ClientPipelineExtensions
                        {
                        }
                    }
                    """)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var client = new TestTypeProvider("SampleClient", TypeSignatureModifiers.Public, ns: "Sample");
            var pipelineExtensions = new TestTypeProvider("ClientPipelineExtensions", TypeSignatureModifiers.Internal | TypeSignatureModifiers.Static, ns: "Sample");
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(client, pipelineExtensions),
                lastContractCompilation: () => Task.FromResult<Compilation>(lastContractCompilation));
            CodeModelGenerator.Instance.AddTypeToKeep(client.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([client, pipelineExtensions]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(client));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(pipelineExtensions));
        }

        [Test]
        public void NamespaceLessCustomCodeBodyDependencyDoesNotRootGeneratedTypeInDifferentNamespace()
        {
            var customCodeView = new BodyDependencyTestTypeProvider("CustomType", "Sample", CreateNamedType("ReferencedModel", string.Empty));
            var customType = new CustomizableTestTypeProvider("CustomType", TypeSignatureModifiers.Public, customCodeView, ns: "Sample");
            var referencedModel = new TestTypeProvider("ReferencedModel", TypeSignatureModifiers.Public, ns: "Other.Models");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, referencedModel));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([customType, referencedModel]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(customType));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(referencedModel));
        }

        [Test]
        public void NamespaceLessCustomCodeBodyDependencyDoesNotRootInternalModelWithSameNamespaceMemberName()
        {
            var customCodeView = new BodyDependencyTestTypeProvider(
                "CustomSetting",
                "Sample",
                [CreateNamedType("Key", string.Empty)],
                [new PropertyProvider($"", MethodSignatureModifiers.Public, typeof(string), "Key", new AutoPropertyBody(true), TestTypeProvider.Empty)]);
            var customType = new CustomizableTestTypeProvider("CustomSetting", TypeSignatureModifiers.Public, customCodeView, ns: "Sample");
            var generatedKey = new TestTypeProvider("Key", TypeSignatureModifiers.Internal, ns: "Sample");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, generatedKey));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([customType, generatedKey]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(customType));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(generatedKey));
        }

        [Test]
        public void CustomCodeBodyDependencyOnExternalTypeDoesNotRootGeneratedTypeBySimpleName()
        {
            var customCodeView = new BodyDependencyTestTypeProvider("CustomType", "Sample", CreateNamedType("OperationState", "Azure"));
            var customType = new CustomizableTestTypeProvider("CustomType", TypeSignatureModifiers.Public, customCodeView, ns: "Sample");
            var generatedOperationState = new TestTypeProvider("OperationState", TypeSignatureModifiers.Internal, ns: "Sample");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, generatedOperationState));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([customType, generatedOperationState]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(customType));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(generatedOperationState));
        }

        [Test]
        public void PublicCustomSignatureExternalTypeDoesNotPublicizeGeneratedTypeBySimpleName()
        {
            var customCodeView = new SignatureDependencyTestTypeProvider(
                "CustomType",
                TypeSignatureModifiers.Public,
                CreateNamedType("Action", "System"));
            var customType = new CustomizableTestTypeProvider("CustomType", TypeSignatureModifiers.Public, customCodeView, ns: "Sample");
            var generatedAction = new TestTypeProvider("Action", TypeSignatureModifiers.Internal, ns: "Sample.Models");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, generatedAction));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);
            CodeModelGenerator.Instance.AddTypeToKeep(generatedAction.Type.FullyQualifiedName, isRoot: false);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([customType, generatedAction]);

            Assert.IsTrue(generatedAction.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(generatedAction.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public void InternalizeModeDoesNotRemoveUnreferencedProviders()
        {
            var context = new TestTypeProvider("SampleContext", TypeSignatureModifiers.Public);
            var unusedModel = new TestTypeProvider("UnusedModel", TypeSignatureModifiers.Public);
            MockHelpers.LoadMockGenerator(
                createOutputLibrary: () => new TestOutputLibrary(context, unusedModel),
                configuration: "{\"unreferenced-types-handling\":\"internalize\"}");
            CodeModelGenerator.Instance.AddTypeToKeep(context.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([context, unusedModel]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(context));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(unusedModel));
            Assert.IsEmpty(ProviderReferenceMapAnalyzer.LatestResult!.RemoveCandidates);
        }

        [Test]
        public void SerializationProviderInfrastructureRootsUseSerializationProviderRelationship()
        {
            var serializationProvider = new TestTypeProvider("SampleModelSerializer", TypeSignatureModifiers.Public);
            var model = new ClientProvider("SampleModel", serializationProvider);
            var optional = new TestTypeProvider("Optional", TypeSignatureModifiers.Public);
            var modelSerializationExtensions = new TestTypeProvider("ModelSerializationExtensions", TypeSignatureModifiers.Public);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(
                model,
                serializationProvider,
                optional,
                modelSerializationExtensions));

            ProviderReferenceMapAnalyzer.Analyze([model, serializationProvider, optional, modelSerializationExtensions]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(serializationProvider));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(optional));
            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(modelSerializationExtensions));
        }

        [Test]
        public async Task InternalCustomizationTypeDoesNotInternalizeGeneratedTypeWithSameSimpleName()
        {
            var customCompilation = CompilationHelper.LoadCompilation(
                [new TestTypeProvider("Error", TypeSignatureModifiers.Internal, ns: "Custom.Models")]);
            var context = new TestTypeProvider("SampleContext", TypeSignatureModifiers.Public);
            var generatedError = new TestTypeProvider("Error", TypeSignatureModifiers.Public, ns: "Generated.Models");
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(context, generatedError),
                compilation: () => Task.FromResult(customCompilation));
            CodeModelGenerator.Instance.AddTypeToKeep(context.Type.FullyQualifiedName);
            CodeModelGenerator.Instance.AddTypeToKeep(generatedError.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([context, generatedError]);

            Assert.IsTrue(generatedError.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsFalse(generatedError.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
        }

        [Test]
        public async Task PublicGenericCustomizationDoesNotPublicizeGeneratedNonGenericTypeWithSameSimpleName()
        {
            var customCompilation = CSharpCompilation.Create(
                "TestAssembly",
                [CSharpSyntaxTree.ParseText("""
                    namespace Generated.Models
                    {
                        public partial class SearchResult<T>
                        {
                        }
                    }
                    """)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var context = new TestTypeProvider("SampleContext", TypeSignatureModifiers.Public);
            var generatedSearchResult = new TestTypeProvider("SearchResult", TypeSignatureModifiers.Internal, ns: "Generated.Models");
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(context, generatedSearchResult),
                compilation: () => Task.FromResult<Compilation>(customCompilation));
            CodeModelGenerator.Instance.AddTypeToKeep(context.Type.FullyQualifiedName);
            CodeModelGenerator.Instance.AddTypeToKeep(generatedSearchResult.Type.FullyQualifiedName, isRoot: false);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([context, generatedSearchResult]);

            Assert.IsTrue(generatedSearchResult.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(generatedSearchResult.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public void PublicGenericCustomSignatureDoesNotRootGeneratedNonGenericTypeBySimpleName()
        {
            var genericArgument = CreateNamedType("T", string.Empty);
            var customCodeView = new SignatureDependencyTestTypeProvider(
                "CustomType",
                TypeSignatureModifiers.Public,
                CreateNamedType("SearchResult", "Generated.Models", genericArgument));
            var customType = new CustomizableTestTypeProvider("CustomType", TypeSignatureModifiers.Public, customCodeView, ns: "Generated.Models");
            var generatedSearchResult = new TestTypeProvider("SearchResult", TypeSignatureModifiers.Internal, ns: "Generated.Models");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, generatedSearchResult));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);
            CodeModelGenerator.Instance.AddTypeToKeep(generatedSearchResult.Type.FullyQualifiedName, isRoot: false);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([customType, generatedSearchResult]);

            Assert.IsTrue(generatedSearchResult.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(generatedSearchResult.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public void DependenciesOfInternalizedClientAreNotPublicizedFromClientRootTraversal()
        {
            var clientOptions = new TestTypeProvider("SampleClientOptions", TypeSignatureModifiers.Public, ns: "Sample");
            var client = new ClientProvider(
                "SampleClient",
                "Sample",
                new SignatureDependencyTestTypeProvider("SampleClient", TypeSignatureModifiers.Internal, ns: "Sample"),
                signatureDependencyTypes: clientOptions.Type);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(client, clientOptions));

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([client, clientOptions]);

            Assert.IsTrue(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public void PublicConstructorOnInternalClientDoesNotPublicizeGeneratedOptions()
        {
            var clientOptions = new TestTypeProvider("SampleClientOptions", TypeSignatureModifiers.Public, ns: "Sample");
            var customCodeView = new SignatureDependencyTestTypeProvider("SampleClient", TypeSignatureModifiers.Internal, ns: "Sample");
            var client = new ConstructorDependencyTestTypeProvider(
                "SampleClient",
                TypeSignatureModifiers.Public,
                customCodeView,
                ns: "Sample",
                constructorParameterTypes: [CreateNamedType("SampleClientOptions", "Sample")]);
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(client, clientOptions));

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([client, clientOptions]);

            Assert.IsTrue(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public async Task GeneratedTypeInternalInLastContractIsInternalized()
        {
            var lastContractCompilation = CSharpCompilation.Create(
                "LastContract",
                [CSharpSyntaxTree.ParseText("""
                    namespace Sample
                    {
                        internal partial class SampleClient
                        {
                        }

                        internal partial class SampleClientOptions
                        {
                            internal enum ServiceVersion
                            {
                                V1 = 1
                            }
                        }
                    }
                    """)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var clientOptions = new TestTypeProvider("SampleClientOptions", TypeSignatureModifiers.Public, ns: "Sample");
            clientOptions.NestedTypesInternal =
            [
                new NestedTestTypeProvider("ServiceVersion", TypeSignatureModifiers.Public | TypeSignatureModifiers.Enum, clientOptions, ns: "Sample")
            ];
            var client = new ClientProvider("SampleClient", "Sample", signatureDependencyTypes: CreateNamedType("SampleClientOptions", "Sample"));
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(client, clientOptions),
                lastContractCompilation: () => Task.FromResult<Compilation>(lastContractCompilation));

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([client, clientOptions]);

            Assert.IsTrue(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(clientOptions.NestedTypes[0].DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.NestedTypes[0].DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public async Task PublicCustomPartialKeepsGeneratedTypePublicWhenLastContractGeneratedDeclarationIsInternal()
        {
            var lastContractCompilation = CSharpCompilation.Create(
                "LastContract",
                [CSharpSyntaxTree.ParseText("""
                    namespace Sample
                    {
                        internal abstract partial class AbstractModel
                        {
                        }
                    }
                    """)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var customCodeView = new TestTypeProvider("AbstractModel", TypeSignatureModifiers.Public | TypeSignatureModifiers.Abstract, ns: "Sample");
            var model = new CustomizableTestTypeProvider("AbstractModel", TypeSignatureModifiers.Public | TypeSignatureModifiers.Abstract, customCodeView, ns: "Sample");
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(model),
                lastContractCompilation: () => Task.FromResult<Compilation>(lastContractCompilation));

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([model]);

            Assert.IsTrue(model.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsFalse(model.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
        }

        [Test]
        public async Task TopLevelTypeWithSameNameAsNestedTypeDoesNotPublicizeDeclaringType()
        {
            var lastContractCompilation = CSharpCompilation.Create(
                "LastContract",
                [CSharpSyntaxTree.ParseText("""
                    namespace Sample
                    {
                        public enum ServiceVersion
                        {
                            V1 = 1
                        }

                        internal partial class SampleClientOptions
                        {
                            internal enum ServiceVersion
                            {
                                V1 = 1
                            }
                        }
                    }
                    """)],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var publicServiceVersion = new TestTypeProvider("ServiceVersion", TypeSignatureModifiers.Public | TypeSignatureModifiers.Enum, ns: "Sample");
            var clientOptions = new TestTypeProvider("SampleClientOptions", TypeSignatureModifiers.Public, ns: "Sample");
            clientOptions.NestedTypesInternal =
            [
                new NestedTestTypeProvider("ServiceVersion", TypeSignatureModifiers.Public | TypeSignatureModifiers.Enum, clientOptions, ns: "Sample")
            ];
            var customPublicApi = new SignatureDependencyTestTypeProvider(
                "CustomPublicApi",
                TypeSignatureModifiers.Public,
                ns: "Sample",
                CreateNamedType("ServiceVersion", "Sample"));
            var generatedPublicApi = new CustomizableTestTypeProvider(
                "CustomPublicApi",
                TypeSignatureModifiers.Public,
                customPublicApi,
                ns: "Sample");
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(publicServiceVersion, clientOptions, generatedPublicApi),
                lastContractCompilation: () => Task.FromResult<Compilation>(lastContractCompilation));

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([publicServiceVersion, clientOptions, generatedPublicApi]);

            Assert.IsTrue(publicServiceVersion.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsFalse(publicServiceVersion.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsTrue(generatedPublicApi.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsFalse(generatedPublicApi.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsTrue(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(clientOptions.NestedTypes[0].DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.NestedTypes[0].DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public async Task GeneratedTypeInternalInExistingSourceIsInternalized()
        {
            var outputPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ReferenceMap", nameof(GeneratedTypeInternalInExistingSourceIsInternalized));
            var projectPath = Path.Combine(outputPath, "src");
            Directory.CreateDirectory(projectPath);
            await File.WriteAllTextAsync(Path.Combine(projectPath, "SampleClient.cs"), """
                namespace Sample
                {
                    internal partial class SampleClient
                    {
                    }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(projectPath, "SampleClientOptions.cs"), """
                namespace Sample
                {
                    internal partial class SampleClientOptions
                    {
                        internal enum ServiceVersion
                        {
                            V1 = 1
                        }
                    }
                }
                """);
            var clientOptions = new TestTypeProvider("SampleClientOptions", TypeSignatureModifiers.Public, ns: "Sample");
            clientOptions.NestedTypesInternal =
            [
                new NestedTestTypeProvider("ServiceVersion", TypeSignatureModifiers.Public | TypeSignatureModifiers.Enum, clientOptions, ns: "Sample")
            ];
            var client = new ClientProvider("SampleClient", "Sample", signatureDependencyTypes: CreateNamedType("SampleClientOptions", "Sample"));
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(client, clientOptions),
                configuration: "{}",
                outputPath: outputPath);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([client, clientOptions]);

            Assert.IsTrue(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(client.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(clientOptions.NestedTypes[0].DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(clientOptions.NestedTypes[0].DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public void PublicCustomCodeArraySignatureKeepsGeneratedTypePublic()
        {
            var customCodeView = new SignatureDependencyTestTypeProvider("PublicCustomApi", TypeSignatureModifiers.Public, CreateNamedType("GeneratedModel", string.Empty));
            var generatedModel = new CustomizableTestTypeProvider("GeneratedModel", TypeSignatureModifiers.Public, customCodeView, ns: "Generated.Models");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(generatedModel));

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([generatedModel]);

            Assert.IsTrue(generatedModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsFalse(generatedModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
        }

        [Test]
        public async Task InternalizedGeneratedPredecessorDoesNotPublicizeItsInternalDependency()
        {
            var outputPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ReferenceMap", nameof(InternalizedGeneratedPredecessorDoesNotPublicizeItsInternalDependency));
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }
            var projectPath = Path.Combine(outputPath, "src", "Generated", "Models");
            Directory.CreateDirectory(projectPath);
            await File.WriteAllTextAsync(Path.Combine(projectPath, "InternalModel.cs"), """
                namespace Sample.Models
                {
                    internal partial class InternalModel
                    {
                    }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(projectPath, "InternalDependency.cs"), """
                namespace Sample.Models
                {
                    internal partial class InternalDependency
                    {
                    }
                }
                """);

            var dependency = new GeneratedModelTestTypeProvider("InternalDependency", TypeSignatureModifiers.Public, ns: "Sample.Models");
            var internalModel = new GeneratedModelTestTypeProvider(
                "InternalModel",
                TypeSignatureModifiers.Public,
                ns: "Sample.Models");
            var publicRoot = new GeneratedModelTestTypeProvider(
                "PublicRoot",
                TypeSignatureModifiers.Public,
                ns: "Sample.Models");
            await MockHelpers.LoadMockGeneratorAsync(
                createOutputLibrary: () => new TestOutputLibrary(publicRoot, internalModel, dependency),
                configuration: "{\"unreferenced-types-handling\":\"removeOrInternalize\"}",
                outputPath: outputPath);
            CodeModelGenerator.Instance.AddTypeToKeep(publicRoot.Type.FullyQualifiedName);
            CodeModelGenerator.Instance.AddTypeToKeep(internalModel.Type.FullyQualifiedName);
            CodeModelGenerator.Instance.AddTypeToKeep(dependency.Type.FullyQualifiedName);

            publicRoot.Update(properties: [
                new PropertyProvider($"", MethodSignatureModifiers.Public, internalModel.Type, "InternalModel", new AutoPropertyBody(false), publicRoot)
            ]);
            internalModel.Update(properties: [
                new PropertyProvider($"", MethodSignatureModifiers.Public, dependency.Type, "Dependency", new AutoPropertyBody(false), internalModel)
            ]);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([internalModel, dependency]);

            Assert.IsTrue(internalModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(internalModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsTrue(dependency.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(dependency.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
        }

        [Test]
        public void InternalizedGenericModelRemovesAritylessModelFactoryMethod()
        {
            var genericArgument = CreateNamedType("T", string.Empty);
            var genericModel = new GenericTestTypeProvider(
                "GenericModel`1",
                TypeSignatureModifiers.Public,
                "Sample.Models",
                genericArgument);
            var outputLibrary = new TestOutputLibrary(genericModel);
            MockHelpers.LoadMockGenerator(
                createOutputLibrary: () => outputLibrary,
                configuration: "{\"unreferenced-types-handling\":\"removeOrInternalize\"}");
            var modelFactory = CodeModelGenerator.Instance.OutputLibrary.ModelFactory.Value;
            modelFactory.Update(methods:
            [
                new MethodProvider(
                    new MethodSignature(
                        "GenericModel",
                        $"",
                        MethodSignatureModifiers.Static | MethodSignatureModifiers.Public,
                        genericModel.Type,
                        $"",
                        []),
                    MethodBodyStatement.Empty,
                    modelFactory)
            ]);

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([genericModel]);

            Assert.IsTrue(genericModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
            Assert.IsFalse(genericModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsEmpty(modelFactory.Methods.Select(m => m.Signature.Name));
        }

        private sealed class BodyDependencyTestTypeProvider : TestTypeProvider
        {
            private readonly CSharpType[] _bodyDependencyTypes;

            public BodyDependencyTestTypeProvider(string name, params CSharpType[] bodyDependencyTypes)
                : this(name, bodyDependencyTypes, ns: null)
            {
            }

            public BodyDependencyTestTypeProvider(string name, string? ns, params CSharpType[] bodyDependencyTypes)
                : this(name, bodyDependencyTypes, ns)
            {
            }

            public BodyDependencyTestTypeProvider(string name, string? ns, CSharpType[] bodyDependencyTypes, PropertyProvider[] properties)
                : base(name, TypeSignatureModifiers.Public, properties: properties, ns: ns)
            {
                _bodyDependencyTypes = bodyDependencyTypes;
            }

            public BodyDependencyTestTypeProvider(string name, CSharpType[] bodyDependencyTypes, string? ns)
                : base(name, TypeSignatureModifiers.Public, ns: ns)
            {
                _bodyDependencyTypes = bodyDependencyTypes;
            }

            protected internal override IReadOnlyList<CSharpType> BuildBodyDependencyTypes() => _bodyDependencyTypes;
        }

        private sealed class HelperDependencyTestTypeProvider : TestTypeProvider
        {
            private readonly CSharpType[] _helperDependencyTypes;

            public HelperDependencyTestTypeProvider(string name, string? ns, params CSharpType[] helperDependencyTypes)
                : base(name, TypeSignatureModifiers.Public, ns: ns)
            {
                _helperDependencyTypes = helperDependencyTypes;
            }

            protected internal override IReadOnlyList<CSharpType> BuildHelperDependencyTypes() => _helperDependencyTypes;
        }

        private sealed class SignatureDependencyTestTypeProvider : TestTypeProvider
        {
            private readonly CSharpType[] _signatureDependencyTypes;

            public SignatureDependencyTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, params CSharpType[] signatureDependencyTypes)
                : this(name, declarationModifiers, ns: null, signatureDependencyTypes)
            {
            }

            public SignatureDependencyTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, string? ns, params CSharpType[] signatureDependencyTypes)
                : base(name, declarationModifiers, ns: ns)
            {
                _signatureDependencyTypes = signatureDependencyTypes;
            }

            protected internal override IReadOnlyList<CSharpType> BuildSignatureDependencyTypes() => _signatureDependencyTypes;
        }

        private sealed class CustomizableTestTypeProvider : TestTypeProvider
        {
            private readonly TypeProvider _customCodeView;

            public CustomizableTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, TypeProvider customCodeView, string ns)
                : base(name, declarationModifiers, ns: ns)
            {
                _customCodeView = customCodeView;
            }

            private protected override TypeProvider? BuildCustomCodeView(string? generatedTypeName = default, string? generatedTypeNamespace = default) => _customCodeView;
        }

        private sealed class ConstructorDependencyTestTypeProvider : TestTypeProvider
        {
            private readonly TypeProvider _customCodeView;
            private readonly CSharpType[] _constructorParameterTypes;

            public ConstructorDependencyTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, TypeProvider customCodeView, string ns, params CSharpType[] constructorParameterTypes)
                : base(name, declarationModifiers, ns: ns)
            {
                _customCodeView = customCodeView;
                _constructorParameterTypes = constructorParameterTypes;
            }

            protected internal override ConstructorProvider[] BuildConstructors()
            {
                var parameters = _constructorParameterTypes
                    .Select((type, index) => new ParameterProvider($"p{index}", $"", type))
                    .ToArray();
                return
                [
                    new ConstructorProvider(
                        new ConstructorSignature(Type, $"", MethodSignatureModifiers.Public, parameters),
                        MethodBodyStatement.Empty,
                        this)
                ];
            }

            private protected override TypeProvider? BuildCustomCodeView(string? generatedTypeName = default, string? generatedTypeNamespace = default) => _customCodeView;
        }

        private sealed class NestedTestTypeProvider : TestTypeProvider
        {
            private readonly TypeProvider _declaringTypeProvider;

            public NestedTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, TypeProvider declaringTypeProvider, string ns)
                : base(name, declarationModifiers, ns: ns)
            {
                _declaringTypeProvider = declaringTypeProvider;
            }

            protected override TypeProvider? BuildDeclaringTypeProvider() => _declaringTypeProvider;
        }

        private sealed class GeneratedModelTestTypeProvider : TypeProvider
        {
            private readonly string _name;
            private readonly string _namespace;
            private readonly TypeSignatureModifiers _declarationModifiers;

            public GeneratedModelTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, string ns)
            {
                _name = name;
                _namespace = ns;
                _declarationModifiers = declarationModifiers;
            }

            protected override string BuildRelativeFilePath() => Path.Combine("src", "Generated", "Models", $"{Name}.cs");
            protected override string BuildName() => _name;
            protected override string BuildNamespace() => _namespace;
            protected override TypeSignatureModifiers BuildDeclarationModifiers() => _declarationModifiers;
        }

        private sealed class GenericTestTypeProvider : TestTypeProvider
        {
            private readonly CSharpType[] _typeArguments;

            public GenericTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, string ns, params CSharpType[] typeArguments)
                : base(name, declarationModifiers, ns: ns)
            {
                _typeArguments = typeArguments;
            }

            protected override CSharpType[] GetTypeArguments() => _typeArguments;
        }

        private sealed class ClientProvider : TestTypeProvider
        {
            private readonly TypeProvider[] _serializationProviders;
            private readonly TypeProvider? _customCodeView;
            private readonly CSharpType[] _signatureDependencyTypes;

            public ClientProvider(string name, params TypeProvider[] serializationProviders)
                : this(name, ns: null, customCodeView: null, serializationProviders: serializationProviders)
            {
            }

            public ClientProvider(string name, string? ns, TypeProvider? customCodeView = null, TypeProvider[]? serializationProviders = null, params CSharpType[] signatureDependencyTypes)
                : base(name, TypeSignatureModifiers.Public, ns: ns)
            {
                _serializationProviders = serializationProviders ?? [];
                _customCodeView = customCodeView;
                _signatureDependencyTypes = signatureDependencyTypes;
            }

            protected override TypeProvider[] BuildSerializationProviders() => _serializationProviders;
            private protected override TypeProvider? BuildCustomCodeView(string? generatedTypeName = default, string? generatedTypeNamespace = default) => _customCodeView;
            protected internal override IReadOnlyList<CSharpType> BuildSignatureDependencyTypes() => _signatureDependencyTypes;
        }

        private static CSharpType CreateNamedType(string name, string ns, params CSharpType[] arguments)
        {
            var constructor = typeof(CSharpType).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(CSharpType), typeof(IReadOnlyList<CSharpType>), typeof(bool), typeof(bool), typeof(CSharpType), typeof(Type)],
                modifiers: null)!;

            return (CSharpType)constructor.Invoke([name, ns, false, false, null, arguments, true, false, null, null]);
        }
    }
}
