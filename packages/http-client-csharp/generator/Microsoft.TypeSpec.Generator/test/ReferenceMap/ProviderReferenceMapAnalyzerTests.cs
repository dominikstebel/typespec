// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
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
