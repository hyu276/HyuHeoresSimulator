/**
 * GAMEPLAY_SCHEMA_V1
 * Purpose: Defines immutable card and ability content models composed from controlled data primitives.
 * Connections: Registries validate classification and abilities while loaders and dashboards serialize equivalent DTOs.
 * Risk: High because schema compatibility governs content publication, replay references, and future migrations.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;

namespace HyuHeroes.Gameplay.Schema;

public enum ContentStatus
{
    Draft,
    Review,
    Approved,
    Published,
    Deprecated
}

public enum CardType
{
    Unit,
    Action,
    Environment,
    HeroAbility
}

public sealed class GameplayDefinitionHeader
{
    public GameplayDefinitionHeader(
        StableId id,
        int schemaVersion,
        int revision,
        ContentStatus status,
        string localizationKey)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Revision = revision;
        Status = status;
        LocalizationKey = string.IsNullOrWhiteSpace(localizationKey)
            ? throw new ArgumentException("Localization key cannot be empty.", nameof(localizationKey))
            : localizationKey;
    }

    public StableId Id { get; }
    public int SchemaVersion { get; }
    public int Revision { get; }
    public ContentStatus Status { get; }
    public string LocalizationKey { get; }
}

public sealed class Classification
{
    public Classification(
        StableId alignmentId,
        StableId classId,
        StableId? factionId = null,
        IEnumerable<StableId>? tags = null,
        IEnumerable<StableId>? keywords = null)
    {
        AlignmentId = alignmentId;
        ClassId = classId;
        FactionId = factionId;
        Tags = CopyDistinct(tags);
        Keywords = CopyDistinct(keywords);
    }

    public StableId AlignmentId { get; }
    public StableId ClassId { get; }
    public StableId? FactionId { get; }
    public IReadOnlyList<StableId> Tags { get; }
    public IReadOnlyList<StableId> Keywords { get; }

    private static IReadOnlyList<StableId> CopyDistinct(IEnumerable<StableId>? values)
    {
        var result = (values ?? Array.Empty<StableId>()).Distinct().OrderBy(value => value).ToArray();
        return new ReadOnlyCollection<StableId>(result);
    }
}

public sealed class StatBlock
{
    private readonly IReadOnlyDictionary<StableId, decimal> _values;

    public StatBlock(IEnumerable<KeyValuePair<StableId, decimal>>? values = null)
    {
        var dictionary = new Dictionary<StableId, decimal>();
        foreach (var pair in values ?? Array.Empty<KeyValuePair<StableId, decimal>>())
        {
            dictionary.Add(pair.Key, pair.Value);
        }

        _values = new ReadOnlyDictionary<StableId, decimal>(dictionary);
    }

    public IReadOnlyDictionary<StableId, decimal> Values => _values;

    public decimal GetRequired(StableId statId) =>
        _values.TryGetValue(statId, out var value)
            ? value
            : throw new KeyNotFoundException($"Stat '{statId}' is not defined.");
}

public sealed class TriggerSpec
{
    public TriggerSpec(StableId typeId, ParameterBag? parameters = null)
    {
        TypeId = typeId;
        Parameters = parameters ?? new ParameterBag();
    }

    public StableId TypeId { get; }
    public ParameterBag Parameters { get; }
}

public sealed class UsageLimitSpec
{
    public UsageLimitSpec(StableId typeId, ParameterBag? parameters = null)
    {
        TypeId = typeId;
        Parameters = parameters ?? new ParameterBag();
    }

    public StableId TypeId { get; }
    public ParameterBag Parameters { get; }
}

public sealed class DurationSpec
{
    public DurationSpec(StableId typeId, ParameterBag? parameters = null)
    {
        TypeId = typeId;
        Parameters = parameters ?? new ParameterBag();
    }

    public StableId TypeId { get; }
    public ParameterBag Parameters { get; }
}

public sealed class AbilityDefinition
{
    public AbilityDefinition(
        GameplayDefinitionHeader header,
        TriggerSpec trigger,
        IEnumerable<EffectDefinition> effects,
        ConditionNode? condition = null,
        UsageLimitSpec? usageLimit = null,
        DurationSpec? duration = null)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        Condition = condition;
        UsageLimit = usageLimit;
        Duration = duration;

        var effectArray = (effects ?? throw new ArgumentNullException(nameof(effects))).ToArray();
        if (effectArray.Length == 0)
        {
            throw new ArgumentException("Ability must contain at least one effect.", nameof(effects));
        }

        Effects = new ReadOnlyCollection<EffectDefinition>(effectArray);
    }

    public GameplayDefinitionHeader Header { get; }
    public TriggerSpec Trigger { get; }
    public ConditionNode? Condition { get; }
    public IReadOnlyList<EffectDefinition> Effects { get; }
    public UsageLimitSpec? UsageLimit { get; }
    public DurationSpec? Duration { get; }
}

public sealed class AbilityBinding
{
    private AbilityBinding(StableId? referencedAbilityId, AbilityDefinition? inlineAbility)
    {
        ReferencedAbilityId = referencedAbilityId;
        InlineAbility = inlineAbility;
    }

    public StableId? ReferencedAbilityId { get; }
    public AbilityDefinition? InlineAbility { get; }
    public bool IsInline => InlineAbility is not null;

    public static AbilityBinding Reference(StableId abilityId) => new(abilityId, null);

    public static AbilityBinding Inline(AbilityDefinition ability) =>
        new(null, ability ?? throw new ArgumentNullException(nameof(ability)));
}

public sealed class CardDefinition
{
    public CardDefinition(
        GameplayDefinitionHeader header,
        CardType cardType,
        Classification classification,
        StatBlock baseStats,
        IEnumerable<AbilityBinding>? abilities = null,
        string? artworkReferenceId = null)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        CardType = cardType;
        Classification = classification ?? throw new ArgumentNullException(nameof(classification));
        BaseStats = baseStats ?? throw new ArgumentNullException(nameof(baseStats));
        Abilities = new ReadOnlyCollection<AbilityBinding>((abilities ?? Array.Empty<AbilityBinding>()).ToArray());
        ArtworkReferenceId = artworkReferenceId;
    }

    public GameplayDefinitionHeader Header { get; }
    public CardType CardType { get; }
    public Classification Classification { get; }
    public StatBlock BaseStats { get; }
    public IReadOnlyList<AbilityBinding> Abilities { get; }
    public string? ArtworkReferenceId { get; }
}
