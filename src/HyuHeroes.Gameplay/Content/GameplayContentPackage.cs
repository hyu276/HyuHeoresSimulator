/**
 * GAMEPLAY_CONTENT_PACKAGE
 * Purpose: Defines the immutable published content snapshot loaded by authoritative simulation and presentation clients.
 * Connections: Captures registry vocabulary plus published AbilityDefinition/CardDefinition content; canonical JSON serialization computes contentHash over this model.
 * Risk: High because package identity and validation define which exact content set a replay/server/client is allowed to execute.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Content;

public sealed class GameplayRegistrySnapshot
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<StableId>> _groups;

    public GameplayRegistrySnapshot(IEnumerable<KeyValuePair<string, IEnumerable<StableId>>> groups)
    {
        if (groups is null) throw new ArgumentNullException(nameof(groups));

        var dictionary = new SortedDictionary<string, IReadOnlyList<StableId>>(StringComparer.Ordinal);
        foreach (var pair in groups)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException("Registry snapshot group names cannot be empty.", nameof(groups));
            }

            var ids = (pair.Value ?? throw new ArgumentException("Registry snapshot group values cannot be null.", nameof(groups)))
                .ToArray();
            if (ids.Any(id => id == default))
            {
                throw new ArgumentException("Registry snapshot IDs cannot contain default values.", nameof(groups));
            }

            if (ids.Distinct().Count() != ids.Length)
            {
                throw new ArgumentException($"Registry snapshot group '{pair.Key}' contains duplicate IDs.", nameof(groups));
            }

            dictionary.Add(
                pair.Key,
                new ReadOnlyCollection<StableId>(ids.OrderBy(id => id).ToArray()));
        }

        _groups = new ReadOnlyDictionary<string, IReadOnlyList<StableId>>(dictionary);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<StableId>> Groups => _groups;

    public IReadOnlyList<StableId> GetRequired(string groupName) =>
        _groups.TryGetValue(groupName, out var ids)
            ? ids
            : throw new KeyNotFoundException($"Registry snapshot group '{groupName}' is missing.");

    public static GameplayRegistrySnapshot FromCatalog(GameplayRegistryCatalog catalog)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        return new GameplayRegistrySnapshot(new[]
        {
            Group("alignments", catalog.Alignments.Entries.Select(entry => entry.Id)),
            Group("classes", catalog.Classes.Entries.Select(entry => entry.Id)),
            Group("factions", catalog.Factions.Entries.Select(entry => entry.Id)),
            Group("tags", catalog.Tags.Entries.Select(entry => entry.Id)),
            Group("keywords", catalog.Keywords.Entries.Select(entry => entry.Id)),
            Group("stats", catalog.Stats.Entries.Select(entry => entry.Id)),
            Group("resources", catalog.Resources.Entries.Select(entry => entry.Id)),
            Group("phases", catalog.Phases.Entries.Select(entry => entry.Id)),
            Group("formulaVariables", catalog.FormulaVariables.Entries.Select(entry => entry.Id)),
            Group("triggers", catalog.Triggers.Entries.Select(entry => entry.Id)),
            Group("conditions", catalog.Conditions.Entries.Select(entry => entry.Id)),
            Group("durations", catalog.Durations.Entries.Select(entry => entry.Id)),
            Group("usageLimits", catalog.UsageLimits.Entries.Select(entry => entry.Id)),
            Group("formulaOperators", catalog.FormulaOperators.Entries.Select(entry => entry.Id)),
            Group("effects", catalog.Effects.Entries.Select(entry => entry.Id))
        });
    }

    public bool ContentEquals(GameplayRegistrySnapshot other)
    {
        if (other is null || Groups.Count != other.Groups.Count)
        {
            return false;
        }

        return Groups.All(pair =>
            other.Groups.TryGetValue(pair.Key, out var ids) &&
            pair.Value.SequenceEqual(ids));
    }

    private static KeyValuePair<string, IEnumerable<StableId>> Group(
        string name,
        IEnumerable<StableId> ids) => new(name, ids);
}

public sealed class GameplayContentPackage
{
    public GameplayContentPackage(
        int schemaVersion,
        string contentVersion,
        DateTimeOffset publishedAt,
        GameplayRegistrySnapshot registries,
        IEnumerable<AbilityDefinition>? abilities,
        IEnumerable<CardDefinition>? cards,
        string? contentHash = null)
    {
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(contentVersion))
        {
            throw new ArgumentException("Content version cannot be empty.", nameof(contentVersion));
        }

        SchemaVersion = schemaVersion;
        ContentVersion = contentVersion;
        PublishedAt = publishedAt.ToUniversalTime();
        Registries = registries ?? throw new ArgumentNullException(nameof(registries));
        Abilities = CopyDefinitions(
            abilities,
            ability => ability.Header.Id,
            nameof(abilities));
        Cards = CopyDefinitions(
            cards,
            card => card.Header.Id,
            nameof(cards));
        ContentHash = string.IsNullOrWhiteSpace(contentHash) ? null : contentHash;
    }

    public int SchemaVersion { get; }
    public string ContentVersion { get; }
    public DateTimeOffset PublishedAt { get; }
    public GameplayRegistrySnapshot Registries { get; }
    public IReadOnlyList<AbilityDefinition> Abilities { get; }
    public IReadOnlyList<CardDefinition> Cards { get; }
    public string? ContentHash { get; }

    public GameplayContentPackage WithContentHash(string contentHash) =>
        new(
            SchemaVersion,
            ContentVersion,
            PublishedAt,
            Registries,
            Abilities,
            Cards,
            contentHash);

    private static IReadOnlyList<TDefinition> CopyDefinitions<TDefinition>(
        IEnumerable<TDefinition>? definitions,
        Func<TDefinition, StableId> idSelector,
        string parameterName)
        where TDefinition : class
    {
        var values = (definitions ?? Array.Empty<TDefinition>()).ToArray();
        if (values.Any(value => value is null))
        {
            throw new ArgumentException("Content definition collections cannot contain null values.", parameterName);
        }

        if (values.GroupBy(idSelector).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Content definition IDs must be unique within a package.", parameterName);
        }

        return new ReadOnlyCollection<TDefinition>(
            values.OrderBy(idSelector).ToArray());
    }
}

public static class GameplayContentPackageValidator
{
    public static void Validate(GameplayContentPackage package, GameplayRegistryCatalog catalog)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        if (package.SchemaVersion != GameplaySchemaValidator.SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Package schemaVersion '{package.SchemaVersion}' is unsupported; expected '{GameplaySchemaValidator.SupportedSchemaVersion}'.");
        }

        var expectedRegistries = GameplayRegistrySnapshot.FromCatalog(catalog);
        if (!package.Registries.ContentEquals(expectedRegistries))
        {
            throw new InvalidDataException("Package registry snapshot does not match the authoritative schema registry catalog.");
        }

        var validator = new GameplaySchemaValidator(catalog);
        foreach (var ability in package.Abilities)
        {
            RequirePublished(ability.Header, "ability");
            RequireValid(validator.Validate(ability), ability.Header.Id);
        }

        foreach (var card in package.Cards)
        {
            RequirePublished(card.Header, "card");
            RequireValid(validator.Validate(card), card.Header.Id);
            ValidateBindings(card, package.Abilities);
        }
    }

    private static void ValidateBindings(
        CardDefinition card,
        IReadOnlyList<AbilityDefinition> packageAbilities)
    {
        var abilityIds = packageAbilities.Select(ability => ability.Header.Id).ToHashSet();
        foreach (var binding in card.Abilities)
        {
            if (binding.ReferencedAbilityId is { } referencedId && !abilityIds.Contains(referencedId))
            {
                throw new InvalidDataException(
                    $"Card '{card.Header.Id}' references missing package ability '{referencedId}'.");
            }

            if (binding.InlineAbility is { } inline)
            {
                RequirePublished(inline.Header, "inline ability");
            }
        }
    }

    private static void RequirePublished(GameplayDefinitionHeader header, string kind)
    {
        if (header.Status != ContentStatus.Published)
        {
            throw new InvalidDataException(
                $"Canonical content packages may only contain published definitions; {kind} '{header.Id}' is '{header.Status}'.");
        }
    }

    private static void RequireValid(ValidationResult result, StableId definitionId)
    {
        if (result.IsValid)
        {
            return;
        }

        throw new InvalidDataException(
            $"Definition '{definitionId}' failed schema validation: {string.Join(" | ", result.Errors.Select(error => $"{error.Code}@{error.Path}: {error.Message}"))}");
    }
}
