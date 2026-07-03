// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
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
        public void NamespaceLessCustomCodeBodyDependencyDoesNotRootGeneratedTypeBySimpleName()
        {
            var customType = new BodyDependencyTestTypeProvider("CustomType", CreateNamedType("Error", string.Empty));
            var generatedError = new TestTypeProvider("Error", TypeSignatureModifiers.Public, ns: "Sample.Models");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(customType, generatedError));
            CodeModelGenerator.Instance.AddTypeToKeep(customType.Type.FullyQualifiedName);

            ProviderReferenceMapAnalyzer.Analyze([customType, generatedError]);

            Assert.IsTrue(ProviderReferenceMapAnalyzer.ShouldWriteProvider(customType));
            Assert.IsFalse(ProviderReferenceMapAnalyzer.ShouldWriteProvider(generatedError));
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
        public void PublicCustomCodeArraySignatureKeepsGeneratedTypePublic()
        {
            var customCodeView = new SignatureDependencyTestTypeProvider("PublicCustomApi", TypeSignatureModifiers.Public, CreateNamedType("GeneratedModel", string.Empty));
            var generatedModel = new CustomizableTestTypeProvider("GeneratedModel", TypeSignatureModifiers.Public, customCodeView, ns: "Generated.Models");
            MockHelpers.LoadMockGenerator(createOutputLibrary: () => new TestOutputLibrary(generatedModel));

            ProviderReferenceMapAnalyzer.ApplyPreWriteAccessibility([generatedModel]);

            Assert.IsTrue(generatedModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Public));
            Assert.IsFalse(generatedModel.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Internal));
        }

        private sealed class BodyDependencyTestTypeProvider : TestTypeProvider
        {
            private readonly CSharpType[] _bodyDependencyTypes;

            public BodyDependencyTestTypeProvider(string name, params CSharpType[] bodyDependencyTypes)
                : base(name, TypeSignatureModifiers.Public)
            {
                _bodyDependencyTypes = bodyDependencyTypes;
            }

            protected internal override IReadOnlyList<CSharpType> BuildBodyDependencyTypes() => _bodyDependencyTypes;
        }

        private sealed class SignatureDependencyTestTypeProvider : TestTypeProvider
        {
            private readonly CSharpType[] _signatureDependencyTypes;

            public SignatureDependencyTestTypeProvider(string name, TypeSignatureModifiers declarationModifiers, params CSharpType[] signatureDependencyTypes)
                : base(name, declarationModifiers)
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

        private sealed class ClientProvider : TestTypeProvider
        {
            private readonly TypeProvider[] _serializationProviders;

            public ClientProvider(string name, params TypeProvider[] serializationProviders)
                : base(name, TypeSignatureModifiers.Public)
            {
                _serializationProviders = serializationProviders;
            }

            protected override TypeProvider[] BuildSerializationProviders() => _serializationProviders;
        }

        private static CSharpType CreateNamedType(string name, string ns)
        {
            var constructor = typeof(CSharpType).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(CSharpType), typeof(IReadOnlyList<CSharpType>), typeof(bool), typeof(bool), typeof(CSharpType), typeof(Type)],
                modifiers: null)!;

            return (CSharpType)constructor.Invoke([name, ns, false, false, null, new List<CSharpType>(), true, false, null, null]);
        }
    }
}
