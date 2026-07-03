// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

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
    }
}
