// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.TypeSpec.Generator.Expressions;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;

namespace Microsoft.TypeSpec.Generator
{
    internal static partial class ProviderReferenceMapAnalyzer
    {
        private static void AddGeneratedBodyReferences(IReadOnlyList<TypeProvider> providers, ProviderReferenceGraph graph)
        {
            foreach (var (provider, isSerializationProvider) in GetBodyReferenceProviders(providers))
            {
                if (IsModelFactoryProvider(provider) ||
                    !IsGeneratedBodyReferenceCandidate(provider, isSerializationProvider))
                {
                    continue;
                }

                var providerName = GetProviderTypeName(provider.Type);
                if (!graph.Nodes.Contains(providerName))
                {
                    continue;
                }

                AddProviderBodyDependencyTypes(
                    graph.References[providerName],
                    GetNonEnumStructuredBodyReferenceTypes(provider, graph.Nodes),
                    graph.Nodes);
                AddProviderBodyDependencyTypes(graph.References[providerName], provider.BodyDependencyTypes, graph.Nodes);
                AddGeneratedProviderNamespaceBodyDependencyTypes(graph.References[providerName], provider, graph.Nodes);
            }
        }

        private static void AddGeneratedProviderNamespaceBodyDependencyTypes(HashSet<string> references, TypeProvider provider, HashSet<string> nodes)
        {
            var providerNamespace = provider.Type.Namespace;
            if (string.IsNullOrEmpty(providerNamespace))
            {
                return;
            }

            foreach (var dependency in provider.BodyDependencyTypes)
            {
                AddGeneratedProviderNamespaceBodyDependencyType(references, dependency, providerNamespace, nodes);
            }
        }

        private static void AddGeneratedProviderNamespaceBodyDependencyType(HashSet<string> references, CSharpType? dependency, string providerNamespace, HashSet<string> nodes)
        {
            if (dependency == null)
            {
                return;
            }

            AddNamespaceBodyDependencyName(references, providerNamespace, dependency, nodes);
            foreach (var argument in dependency.Arguments)
            {
                AddGeneratedProviderNamespaceBodyDependencyType(references, argument, providerNamespace, nodes);
            }
        }

        private static void AddNamespaceBodyDependencyName(HashSet<string> references, string providerNamespace, CSharpType dependency, HashSet<string> nodes)
        {
            var dependencyName = GetSimpleName(GetProviderTypeName(dependency));
            AddExactMetadataNameMatch(references, $"{providerNamespace}.{dependencyName}", nodes);
            var aritylessDependencyName = StripGenericArity(dependencyName);
            if (!string.Equals(aritylessDependencyName, dependencyName, StringComparison.Ordinal))
            {
                AddExactMetadataNameMatch(references, $"{providerNamespace}.{aritylessDependencyName}", nodes);
            }
        }

        private static IReadOnlyList<CSharpType> GetNonEnumStructuredBodyReferenceTypes(TypeProvider provider, HashSet<string> nodes)
        {
            var references = new List<CSharpType>();
            foreach (var dependency in CollectStructuredBodyReferenceTypes(provider))
            {
                if (!IsEnumProviderDependency(dependency, nodes))
                {
                    references.Add(dependency);
                }
            }

            return references;
        }

        private static IReadOnlyList<CSharpType> CollectStructuredBodyReferenceTypes(TypeProvider provider)
        {
            var references = new HashSet<CSharpType>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var field in provider.Fields)
            {
                CollectStructuredBodyReferenceTypes(field.InitializationValue, references, visited);
            }

            foreach (var property in provider.Properties)
            {
                CollectStructuredBodyReferenceTypes(property.Body, references, visited);
            }

            foreach (var constructor in provider.Constructors)
            {
                CollectStructuredBodyReferenceTypes(constructor.BodyExpression, references, visited);
                CollectStructuredBodyReferenceTypes(constructor.BodyStatements, references, visited);
            }

            foreach (var method in provider.Methods)
            {
                if (method.IsMethodSuppressed())
                {
                    continue;
                }

                CollectStructuredBodyReferenceTypes(method.BodyExpression, references, visited);
                CollectStructuredBodyReferenceTypes(method.BodyStatements, references, visited);
            }

            return [.. references];
        }

        private static void CollectStructuredBodyReferenceTypes(object? value, HashSet<CSharpType> references, HashSet<object> visited)
        {
            switch (value)
            {
                case null:
                case string:
                case FormattableString:
                    return;
            }

            if (!value.GetType().IsValueType && !visited.Add(value))
            {
                return;
            }

            switch (value)
            {
                case CSharpType type:
                    references.Add(type);
                    return;
                case Type type:
                    references.Add(type);
                    return;
                case ParameterProvider parameter:
                    references.Add(parameter.Type);
                    CollectStructuredBodyReferenceTypes(parameter.DefaultValue, references, visited);
                    CollectStructuredBodyReferenceTypes(parameter.InitializationValue, references, visited);
                    return;
                case MethodSignatureBase signature:
                    CollectStructuredBodyReferenceTypes(signature.ReturnType, references, visited);
                    CollectStructuredBodyReferenceTypes(signature.Parameters, references, visited);
                    return;
                case KeyValuePair<string, ValueExpression> positionalArgument:
                    CollectStructuredBodyReferenceTypes(positionalArgument.Value, references, visited);
                    return;
                case FieldProvider field:
                    references.Add(field.Type);
                    CollectStructuredBodyReferenceTypes(field.InitializationValue, references, visited);
                    return;
            }

            if (IsStructuredBodyReferenceObject(value))
            {
                foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    CollectStructuredBodyReferenceTypes(property.GetValue(value), references, visited);
                }

                return;
            }

            if (value is not IEnumerable values)
            {
                return;
            }

            foreach (var item in values)
            {
                CollectStructuredBodyReferenceTypes(item, references, visited);
            }
        }

        private static bool IsEnumProviderDependency(CSharpType dependency, HashSet<string> nodes)
        {
            var providerName = GetProviderTypeName(dependency);
            if (!nodes.Contains(providerName))
            {
                return false;
            }

            foreach (var provider in CodeModelGenerator.Instance.OutputLibrary.TypeProviders)
            {
                if (provider is EnumProvider &&
                    string.Equals(GetProviderTypeName(provider.Type), providerName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStructuredBodyReferenceObject(object value) =>
            value is ValueExpression ||
            value is MethodBodyStatement ||
            value is PropertyBody;

        private static void AddProviderBodyDependencyTypes(
            HashSet<string> references,
            IReadOnlyList<CSharpType> dependencies,
            HashSet<string> nodes,
            bool includeSimpleNameReferences = false,
            bool includeUnqualifiedSimpleNameReferences = false,
            bool includeExtensionReferences = true)
        {
            foreach (var dependency in dependencies)
            {
                AddProviderBodyDependencyType(references, dependency, nodes, includeSimpleNameReferences, includeUnqualifiedSimpleNameReferences, includeExtensionReferences);
            }
        }

        private static void AddProviderBodyDependencyType(
            HashSet<string> references,
            CSharpType? dependency,
            HashSet<string> nodes,
            bool includeSimpleNameReferences,
            bool includeUnqualifiedSimpleNameReferences,
            bool includeExtensionReferences)
        {
            if (dependency == null)
            {
                return;
            }

            AddTypeReference(references, dependency, nodes);
            if (includeSimpleNameReferences &&
                !string.IsNullOrEmpty(dependency.Namespace) &&
                dependency.Arguments.Count == 0)
            {
                AddMatchingName(references, dependency.Name, nodes);
            }
            else if (includeUnqualifiedSimpleNameReferences &&
                string.IsNullOrEmpty(dependency.Namespace) &&
                dependency.Arguments.Count == 0)
            {
                AddUnambiguousMatchingName(references, dependency.Name, nodes);
            }
            if (includeExtensionReferences)
            {
                if (string.IsNullOrEmpty(dependency.Namespace))
                {
                    AddMatchingName(references, $"{dependency.Name}Extensions", nodes);
                }
                else
                {
                    AddExactMetadataNameMatch(references, $"{dependency.Namespace}.{dependency.Name}Extensions", nodes);
                }
            }

            foreach (var argument in dependency.Arguments)
            {
                AddProviderBodyDependencyType(references, argument, nodes, includeSimpleNameReferences, includeUnqualifiedSimpleNameReferences, includeExtensionReferences);
            }
        }

        private static IReadOnlyList<(TypeProvider Provider, bool IsSerializationProvider)> GetBodyReferenceProviders(IReadOnlyList<TypeProvider> providers)
        {
            var bodyReferenceProviders = new List<(TypeProvider Provider, bool IsSerializationProvider)>();
            foreach (var provider in providers)
            {
                bodyReferenceProviders.Add((provider, false));
                foreach (var serializationProvider in provider.SerializationProviders)
                {
                    bodyReferenceProviders.Add((serializationProvider, true));
                }
            }

            return bodyReferenceProviders;
        }

        private static bool IsGeneratedBodyReferenceCandidate(TypeProvider provider, bool isSerializationProvider)
        {
            return IsGeneratedImplementationBodyReferenceCandidate(provider, isSerializationProvider) ||
                provider.HelperDependencyTypes.Count > 0 ||
                provider.BodyDependencyTypes.Count > 0;
        }

        private static bool IsGeneratedImplementationBodyReferenceCandidate(TypeProvider provider, bool isSerializationProvider)
        {
            if (provider.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Static) ||
                IsClientProvider(provider) ||
                isSerializationProvider)
            {
                return true;
            }

            return provider is not ModelProvider &&
                provider is not EnumProvider &&
                !IsModelFactoryProvider(provider) &&
                provider.DeclaringTypeProvider == null &&
                provider.SerializationProviders.Count == 0;
        }
    }
}
