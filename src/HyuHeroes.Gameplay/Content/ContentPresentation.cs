/**
 * CONTENT_PRESENTATION
 * Purpose: Carries localization and presentation-only metadata beside gameplay definitions without turning UI concerns into gameplay rules.
 * Connections: GameplayContentPackage includes this snapshot; ContentPublisher requires default-locale coverage before publishing.
 * Risk: Medium because missing localization/assets degrade clients but must never change authoritative gameplay semantics.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;

namespace HyuHeroes.Gameplay.Content;

public sealed class LocalizedDefinitionText
{
    public LocalizedDefinitionText(string localizationKey, string name, string description)
    {
        LocalizationKey = string.IsNullOrWhiteSpace(localizationKey)
            ? throw new ArgumentException("Localization key cannot be empty.", nameof(localizationKey))
            : localizationKey;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Localized name cannot be empty.", nameof(name))
            : name;
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public string LocalizationKey { get; }
    public string Name { get; }
    public string Description { get; }
}

public sealed class LocalizationBundle
{
    private readonly IReadOnlyDictionary<string, LocalizedDefinitionText> _entries;

    public LocalizationBundle(string locale, IEnumerable<LocalizedDefinitionText> entries)
    {
        Locale = NormalizeLocale(locale);
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        var values = entries.ToArray();
        if (values.Any(value => value is null))
        {
            throw new ArgumentException("Localization entries cannot contain null values.", nameof(entries));
        }

        if (values.GroupBy(value => value.LocalizationKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Localization keys must be unique within a locale.", nameof(entries));
        }

        _entries = new ReadOnlyDictionary<string, LocalizedDefinitionText>(
            values.OrderBy(value => value.LocalizationKey, StringComparer.Ordinal)
                .ToDictionary(value => value.LocalizationKey, StringComparer.Ordinal));
    }

    public string Locale { get; }
    public IReadOnlyDictionary<string, LocalizedDefinitionText> Entries => _entries;

    public LocalizedDefinitionText GetRequired(string localizationKey) =>
        _entries.TryGetValue(localizationKey, out var value)
            ? value
            : throw new KeyNotFoundException(
                $"Localization key '{localizationKey}' is missing from locale '{Locale}'.");

    internal static string NormalizeLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            throw new ArgumentException("Locale cannot be empty.", nameof(locale));
        }

        return locale.Trim();
    }
}

public sealed class DefinitionPresentationMetadata
{
    public DefinitionPresentationMetadata(
        StableId definitionId,
        string? artworkReferenceId = null,
        string? iconReferenceId = null,
        string? frameStyle = null)
    {
        DefinitionId = definitionId == default
            ? throw new ArgumentException("Presentation definition ID must be non-default.", nameof(definitionId))
            : definitionId;
        ArtworkReferenceId = NormalizeOptional(artworkReferenceId);
        IconReferenceId = NormalizeOptional(iconReferenceId);
        FrameStyle = NormalizeOptional(frameStyle);
    }

    public StableId DefinitionId { get; }
    public string? ArtworkReferenceId { get; }
    public string? IconReferenceId { get; }
    public string? FrameStyle { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class GameplayPresentationSnapshot
{
    private readonly IReadOnlyDictionary<string, LocalizationBundle> _localizations;
    private readonly IReadOnlyDictionary<StableId, DefinitionPresentationMetadata> _definitions;

    public GameplayPresentationSnapshot(
        string defaultLocale,
        IEnumerable<LocalizationBundle> localizations,
        IEnumerable<DefinitionPresentationMetadata>? definitions = null)
    {
        DefaultLocale = LocalizationBundle.NormalizeLocale(defaultLocale);
        if (localizations is null) throw new ArgumentNullException(nameof(localizations));

        var localeValues = localizations.ToArray();
        if (localeValues.Any(value => value is null))
        {
            throw new ArgumentException("Localization bundles cannot contain null values.", nameof(localizations));
        }

        if (localeValues.GroupBy(value => value.Locale, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Localization bundle locales must be unique.", nameof(localizations));
        }

        var definitionValues = (definitions ?? Array.Empty<DefinitionPresentationMetadata>()).ToArray();
        if (definitionValues.Any(value => value is null))
        {
            throw new ArgumentException("Presentation metadata cannot contain null values.", nameof(definitions));
        }

        if (definitionValues.GroupBy(value => value.DefinitionId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Presentation metadata IDs must be unique.", nameof(definitions));
        }

        _localizations = new ReadOnlyDictionary<string, LocalizationBundle>(
            localeValues.OrderBy(value => value.Locale, StringComparer.Ordinal)
                .ToDictionary(value => value.Locale, StringComparer.Ordinal));
        _definitions = new ReadOnlyDictionary<StableId, DefinitionPresentationMetadata>(
            definitionValues.OrderBy(value => value.DefinitionId)
                .ToDictionary(value => value.DefinitionId));

        if (!_localizations.ContainsKey(DefaultLocale))
        {
            throw new ArgumentException(
                $"Default locale '{DefaultLocale}' is not present in localization bundles.",
                nameof(defaultLocale));
        }
    }

    public string DefaultLocale { get; }
    public IReadOnlyDictionary<string, LocalizationBundle> Localizations => _localizations;
    public IReadOnlyDictionary<StableId, DefinitionPresentationMetadata> Definitions => _definitions;

    public LocalizationBundle GetDefaultBundle() => _localizations[DefaultLocale];

    public LocalizationBundle GetRequiredLocale(string locale)
    {
        var normalized = LocalizationBundle.NormalizeLocale(locale);
        return _localizations.TryGetValue(normalized, out var bundle)
            ? bundle
            : throw new KeyNotFoundException($"Locale '{normalized}' is not published in this content package.");
    }
}
