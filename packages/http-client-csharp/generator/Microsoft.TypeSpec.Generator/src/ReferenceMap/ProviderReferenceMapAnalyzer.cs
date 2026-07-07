// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.TypeSpec.Generator.Expressions;
using Microsoft.TypeSpec.Generator.Primitives;
using Microsoft.TypeSpec.Generator.Providers;
using Microsoft.TypeSpec.Generator.Statements;

namespace Microsoft.TypeSpec.Generator
{
    internal static partial class ProviderReferenceMapAnalyzer
    {
        private static ProviderReferenceMapResult? _latestResult;
        private static readonly ConditionalWeakTable<HashSet<string>, Dictionary<string, string[]>> _simpleNameLookupCache = new();
        private static TypeProvider? _preWriteModelFactory;
        private static MethodProvider[]? _preWriteModelFactoryMethods;
        private static readonly Dictionary<TypeProvider, MethodBodyStatement[]> _preWriteProviderAttributes = new(ReferenceEqualityComparer.Instance);

        public static ProviderReferenceMapResult? LatestResult => _latestResult;

        public static ProviderReferenceMapSession PrepareForGeneration(IReadOnlyList<TypeProvider> providers)
        {
            ResetPreWriteAccessibility();
            ApplyPreWriteAccessibility(providers);
            Analyze(providers);
            FilterPreWriteProviderAttributes(providers);
            return new ProviderReferenceMapSession();
        }

        public static bool ShouldWriteProvider(TypeProvider provider) =>
            _latestResult?.RemoveCandidates.Contains(GetProviderTypeName(provider.Type)) != true;

        public static void ResetPreWriteAccessibility()
        {
            RestorePreWriteModelFactoryMethods();
            RestorePreWriteProviderAttributes();
            _latestResult = null;
        }

        public static void ApplyPreWriteAccessibility(IReadOnlyList<TypeProvider> providers)
        {
            if (Configuration.UnreferencedTypesHandling == Configuration.UnreferencedTypesHandlingOption.KeepAll)
            {
                return;
            }

            // Accessibility has to be adjusted before files are written so provider declarations
            // and model factory signatures are emitted with their final accessibility.
            var (internalizeCandidates, publicizeCandidates) = GetPreWriteAccessibilityCandidates(providers);
            foreach (var provider in GetGeneratedProviders(providers))
            {
                var providerName = GetProviderTypeName(provider.Type);
                if (internalizeCandidates.Contains(providerName))
                {
                    if (provider.DeclaringTypeProvider is null)
                    {
                        provider.PreserveXmlDocs();
                    }
                    provider.Update(modifiers: MakeInternal(provider.DeclarationModifiers));
                }
                else if (publicizeCandidates.Contains(providerName) && !IsGeneratedInternalImplementation(provider))
                {
                    provider.Update(modifiers: MakePublic(provider.DeclarationModifiers));
                }
            }

            RemoveMethodsFromModelFactory(GetSimpleNames(internalizeCandidates));
        }

        public static void RestorePreWriteModelFactoryMethods()
        {
            if (_preWriteModelFactory == null || _preWriteModelFactoryMethods == null)
            {
                return;
            }

            _preWriteModelFactory.Update(methods: _preWriteModelFactoryMethods);
            _preWriteModelFactory = null;
            _preWriteModelFactoryMethods = null;
        }

        private static void RestorePreWriteProviderAttributes()
        {
            if (_preWriteProviderAttributes.Count == 0)
            {
                return;
            }

            foreach (var (provider, attributes) in _preWriteProviderAttributes)
            {
                provider.Update(attributes: attributes);
            }

            _preWriteProviderAttributes.Clear();
        }

        public static void Analyze(IReadOnlyList<TypeProvider> providers)
        {
            var generatedProviders = GetGeneratedProviders(providers);

            // Build two graphs from provider metadata: the full implementation graph for removal,
            // and the public-surface graph for accessibility decisions.
            var graph = BuildGraph(generatedProviders);
            var publicGraph = BuildGraph(generatedProviders, publicOnly: true);

            var customPublicRoots = GetCustomCodePublicGeneratedTypeRoots(generatedProviders, graph.Nodes);
            var apiBaselineGeneratedTypeRoots = GetApiBaselineGeneratedTypeRoots(graph.Nodes);
            customPublicRoots.UnionWith(apiBaselineGeneratedTypeRoots);
            var generatedPublicDeclarations = GetGeneratedPublicTypeDeclarationsFromLastContract(generatedProviders, graph.Nodes);
            customPublicRoots.UnionWith(generatedPublicDeclarations);
            var customCodeRemovalRoots = GetCustomCodeGeneratedTypeRoots(generatedProviders, graph.Nodes);
            var customRemovalRoots = new HashSet<string>(customCodeRemovalRoots, StringComparer.Ordinal);
            customRemovalRoots.UnionWith(apiBaselineGeneratedTypeRoots);
            customRemovalRoots.UnionWith(generatedPublicDeclarations);
            var customInternalDeclarations = GetCustomCodeInternalGeneratedTypeDeclarations(generatedProviders, graph.Nodes);
            var generatedInternalDeclarations = GetGeneratedInternalTypeDeclarations(generatedProviders, graph.Nodes);
            var existingGeneratedInternalDeclarations = GetGeneratedExistingSourceInternalTypeDeclarations(generatedProviders, graph.Nodes);
            customRemovalRoots.UnionWith(GetExistingGeneratedHelperRoots(generatedProviders, existingGeneratedInternalDeclarations));

            // Helper types are rooted after an initial reachability pass so unused infrastructure
            // such as change-tracking dictionaries can still be removed when no reachable type needs them.
            var generatedDiscriminatorBaseNames = GetGeneratedPersistableModelProxyTypeNames(generatedProviders, publicGraph.Nodes);
            var (internalizeCandidates, publicizeCandidates, _) = GetAccessibilityCandidates(
                providers,
                generatedProviders,
                graph,
                publicGraph,
                customPublicRoots,
                customInternalDeclarations,
                generatedInternalDeclarations,
                generatedDiscriminatorBaseNames);

            var removeCandidates = new HashSet<string>(StringComparer.Ordinal);
            if (Configuration.UnreferencedTypesHandling == Configuration.UnreferencedTypesHandlingOption.RemoveOrInternalize)
            {
                // Body-only generated dependencies are needed to avoid deleting helper files, but they do
                // not contribute to public API reachability for internalization.
                AddGeneratedBodyReferences(providers, graph);
                removeCandidates = GetRemovalCandidates(
                    providers,
                    generatedProviders,
                    graph,
                    customRemovalRoots,
                    generatedDiscriminatorBaseNames);
            }

            _latestResult = new ProviderReferenceMapResult(
                internalizeCandidates,
                publicizeCandidates,
                removeCandidates);
            if (Configuration.UnreferencedTypesHandling == Configuration.UnreferencedTypesHandlingOption.RemoveOrInternalize)
            {
                RemoveMethodsFromModelFactory(GetSimpleNames(removeCandidates));
            }
        }

        private static void FilterPreWriteProviderAttributes(IReadOnlyList<TypeProvider> providers)
        {
            if (_latestResult == null || _latestResult.RemoveCandidates.Count == 0)
            {
                return;
            }

            foreach (var provider in GetGeneratedProviders(providers))
            {
                var attributes = provider.GetAttributes();
                var filteredAttributes = attributes
                    .Where(attribute => !HasRemovedTypeOfArgument(attribute, _latestResult.RemoveCandidates))
                    .ToArray();

                if (filteredAttributes.Length == attributes.Count)
                {
                    continue;
                }

                _preWriteProviderAttributes.TryAdd(provider, [.. attributes]);
                provider.Update(attributes: filteredAttributes);
            }
        }

        private static bool HasRemovedTypeOfArgument(MethodBodyStatement statement, HashSet<string> removeCandidates)
        {
            var attribute = statement switch
            {
                AttributeStatement attributeStatement => attributeStatement,
                SuppressionStatement suppression => suppression.AsStatement<AttributeStatement>(),
                _ => null
            };

            if (attribute == null)
            {
                return false;
            }

            foreach (var argument in attribute.Arguments)
            {
                if (IsRemovedTypeOfArgument(argument, removeCandidates))
                {
                    return true;
                }
            }

            foreach (var (_, argument) in attribute.PositionalArguments)
            {
                if (IsRemovedTypeOfArgument(argument, removeCandidates))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRemovedTypeOfArgument(ValueExpression argument, HashSet<string> removeCandidates)
            => argument is TypeOfExpression typeOf &&
                removeCandidates.Contains(GetProviderTypeName(typeOf.Type));

        private static (HashSet<string> InternalizeCandidates, HashSet<string> PublicizeCandidates) GetPreWriteAccessibilityCandidates(IReadOnlyList<TypeProvider> providers)
        {
            var generatedProviders = GetGeneratedProviders(providers);
            var graph = BuildGraph(generatedProviders);
            var publicGraph = BuildGraph(generatedProviders, publicOnly: true);
            var customPublicRoots = GetCustomCodePublicGeneratedTypeRoots(generatedProviders, graph.Nodes);
            var apiBaselineGeneratedTypeRoots = GetApiBaselineGeneratedTypeRoots(graph.Nodes);
            customPublicRoots.UnionWith(apiBaselineGeneratedTypeRoots);
            var generatedPublicDeclarations = GetGeneratedPublicTypeDeclarationsFromLastContract(generatedProviders, graph.Nodes);
            customPublicRoots.UnionWith(generatedPublicDeclarations);
            var customInternalDeclarations = GetCustomCodeInternalGeneratedTypeDeclarations(generatedProviders, graph.Nodes);
            var generatedInternalDeclarations = GetGeneratedInternalTypeDeclarations(generatedProviders, graph.Nodes);
            var generatedDiscriminatorBaseNames = new HashSet<string>(StringComparer.Ordinal);

            var (internalizeCandidates, publicizeCandidates, _) = GetAccessibilityCandidates(
                providers,
                generatedProviders,
                graph,
                publicGraph,
                customPublicRoots,
                customInternalDeclarations,
                generatedInternalDeclarations,
                generatedDiscriminatorBaseNames);

            return (internalizeCandidates, publicizeCandidates);
        }

        private static (HashSet<string> InternalizeCandidates, HashSet<string> PublicizeCandidates, HashSet<string> InternalizeHelperRoots) GetAccessibilityCandidates(
            IReadOnlyList<TypeProvider> providers,
            IReadOnlyList<TypeProvider> generatedProviders,
            ProviderReferenceGraph graph,
            ProviderReferenceGraph publicGraph,
            HashSet<string> customPublicRoots,
            HashSet<string> customInternalDeclarations,
            HashSet<string> generatedInternalDeclarations,
            HashSet<string> generatedDiscriminatorBaseNames)
        {
            var internalizeReferences = CloneReferences(publicGraph.References);

            // Start from public client and custom/public API roots. Anything public-reachable can
            // stay public unless it crosses a custom/internal boundary.
            var internalizeRoots = GetRootNames(providers, graph.Nodes, helperRoots: [], includeModelFactory: false, includeAdditionalRoots: true, includeUnionVariantRoots: false, includeModelFactorySignatureRoots: true, publicClientRootsOnly: true);
            if (ShouldUseUnionVariantFallbackRoots())
            {
                AddUnionVariantRoots(internalizeRoots, providers, graph.Nodes);
            }

            var generatedPublicReachable = GetReachableTypes(internalizeRoots, internalizeReferences);
            AddDerivedModelReferences(providers, publicGraph.Nodes, internalizeReferences, generatedPublicReachable, generatedDiscriminatorBaseNames);
            internalizeRoots.UnionWith(customPublicRoots);
            var internalizeReachableWithoutHelpers = GetReachableTypes(internalizeRoots, internalizeReferences);
            AddDerivedModelReferences(providers, publicGraph.Nodes, internalizeReferences, internalizeReachableWithoutHelpers, generatedDiscriminatorBaseNames);
            internalizeReachableWithoutHelpers = GetReachableTypes(internalizeRoots, internalizeReferences);
            var publicizeRoots = new HashSet<string>(internalizeRoots, StringComparer.Ordinal);
            var publicApiReferences = CloneReferences(publicGraph.References);
            var internalizeHelperRoots = GetHelperRootNames(generatedProviders, graph.Nodes, internalizeReachableWithoutHelpers, graph.References);
            internalizeRoots.UnionWith(internalizeHelperRoots);
            var internalizeDeclaredNodes = GetGeneratedDeclaredNodes(generatedProviders, graph.Nodes, publicOnly: true);
            var customInternalBoundaryNodes = GetCustomInternalBoundaryNodes(publicGraph, customInternalDeclarations);
            var publicizeDeclaredNodes = GetPublicizeDeclaredNodes(generatedProviders, graph.Nodes, internalizeDeclaredNodes);
            var generatedImplementationInternalDeclarations = GetGeneratedImplementationInternalTypeDeclarations(generatedProviders, generatedInternalDeclarations);
            var publicApiTraversalNodes = GetPublicApiTraversalNodes(
                internalizeDeclaredNodes,
                publicizeDeclaredNodes,
                generatedInternalDeclarations,
                generatedImplementationInternalDeclarations);
            var publicizeReachable = GetReachableTypes(publicizeRoots, internalizeReferences, publicApiTraversalNodes);
            var internalizeCandidates = GetInternalizeCandidates(
                internalizeDeclaredNodes,
                publicizeReachable,
                customInternalDeclarations,
                generatedInternalDeclarations,
                customInternalBoundaryNodes,
                customPublicRoots,
                publicizeRoots,
                graph.Nodes,
                internalizeReferences);
            AddNestedInternalizeCandidates(generatedProviders, internalizeCandidates, graph.Nodes);
            AddInternalOnlyDependencyCandidates(
                internalizeDeclaredNodes,
                internalizeCandidates,
                customInternalDeclarations,
                generatedInternalDeclarations,
                customPublicRoots,
                internalizeReferences,
                generatedImplementationInternalDeclarations);
            AddNestedInternalizeCandidates(generatedProviders, internalizeCandidates, graph.Nodes);
            publicizeRoots.ExceptWith(internalizeCandidates);
            publicizeReachable = GetReachableTypes(publicizeRoots, internalizeReferences, publicApiTraversalNodes);
            var publicizeRootExclusions = GetRootNames(
                providers,
                graph.Nodes,
                helperRoots: [],
                includeModelFactory: true,
                includeAdditionalRoots: true,
                includeUnionVariantRoots: true,
                includeModelFactorySignatureRoots: false,
                publicClientRootsOnly: true);
            var publicizeCandidates = GetPublicizeCandidates(
                publicizeDeclaredNodes,
                publicizeReachable,
                customInternalDeclarations,
                customInternalBoundaryNodes,
                internalizeHelperRoots,
                publicizeRootExclusions,
                generatedInternalDeclarations,
                publicizeRoots,
                publicApiReferences,
                internalizeReferences,
                generatedImplementationInternalDeclarations);
            return (internalizeCandidates, publicizeCandidates, internalizeHelperRoots);
        }

        private static bool IsPublic(MethodSignatureModifiers modifiers) => modifiers.HasFlag(MethodSignatureModifiers.Public);
        private static bool IsPublic(FieldModifiers modifiers) => modifiers.HasFlag(FieldModifiers.Public);

        private static TypeSignatureModifiers MakeInternal(TypeSignatureModifiers modifiers)
            => (modifiers & ~(TypeSignatureModifiers.Public | TypeSignatureModifiers.Private | TypeSignatureModifiers.Protected)) | TypeSignatureModifiers.Internal;

        private static TypeSignatureModifiers MakePublic(TypeSignatureModifiers modifiers)
            => (modifiers & ~(TypeSignatureModifiers.Internal | TypeSignatureModifiers.Private | TypeSignatureModifiers.Protected)) | TypeSignatureModifiers.Public;

        private sealed record ProviderReferenceGraph(
            HashSet<string> Nodes,
            Dictionary<string, HashSet<string>> References);
    }
}
