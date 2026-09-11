/**
 * STAT_MODIFIER_PIPELINE
 * Purpose: Resolves effective runtime stats from immutable base values plus deterministic explicit modifier layers.
 * Connections: Consumed by GameplayRuntimeEvaluator, condition predicates, formula variables, effect planning, damage resolution, and lifecycle expiry.
 * Risk: High because modifier ordering, duration metadata, and clamping directly affect authoritative numeric gameplay outcomes and replay determinism.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Modifiers;

public enum StatModifierLayer
{
    PermanentMatch,
    Temporary,
    Contextual
}

public enum StatModifierOperation
{
    Add,
    Multiply,
    Set,
    Minimum,
    Maximum
}

public sealed class StatModifierDurationState
{
    public StatModifierDurationState(
        StableId typeId,
        int createdTurn,
        int? expiresAtTurn = null,
        TargetZone? requiredTargetZone = null,
        long? sourceResidencyEpoch = null,
        long? targetResidencyEpoch = null)
    {
        if (typeId == default)
        {
            throw new ArgumentException("Duration type ID must be a non-default StableId.", nameof(typeId));
        }

        if (createdTurn <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(createdTurn), "Created turn must be positive.");
        }

        if (expiresAtTurn is <= 0 || expiresAtTurn < createdTurn)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtTurn), "Expiry turn must be null or not earlier than the created turn.");
        }

        if (requiredTargetZone is { } zone && !Enum.IsDefined(typeof(TargetZone), zone))
        {
            throw new ArgumentOutOfRangeException(nameof(requiredTargetZone), zone, "Required target zone is not defined.");
        }

        if (sourceResidencyEpoch is <= 0 || targetResidencyEpoch is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceResidencyEpoch), "Residency epochs must be null or positive.");
        }

        TypeId = typeId;
        CreatedTurn = createdTurn;
        ExpiresAtTurn = expiresAtTurn;
        RequiredTargetZone = requiredTargetZone;
        SourceResidencyEpoch = sourceResidencyEpoch;
        TargetResidencyEpoch = targetResidencyEpoch;
    }

    public StableId TypeId { get; }
    public int CreatedTurn { get; }
    public int? ExpiresAtTurn { get; }
    public TargetZone? RequiredTargetZone { get; }
    public long? SourceResidencyEpoch { get; }
    public long? TargetResidencyEpoch { get; }
}

public sealed class StatModifier
{
    public StatModifier(
        StableId instanceId,
        StableId sourceId,
        StableId targetId,
        StableId statId,
        StatModifierOperation operation,
        decimal value,
        StatModifierLayer layer,
        int priority = 0,
        StableId? durationId = null,
        StatModifierDurationState? durationState = null)
    {
        if (instanceId == default || sourceId == default || targetId == default || statId == default)
        {
            throw new ArgumentException("Modifier identity, source, target, and stat IDs must be non-default StableIds.");
        }

        if (!Enum.IsDefined(typeof(StatModifierOperation), operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Stat modifier operation is not defined.");
        }

        if (!Enum.IsDefined(typeof(StatModifierLayer), layer))
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "Stat modifier layer is not defined.");
        }

        if (durationId is { } concreteDurationId && concreteDurationId == default)
        {
            throw new ArgumentException("Duration ID must be null or a non-default StableId.", nameof(durationId));
        }

        if (durationState is not null && durationId is { } configuredDurationId && configuredDurationId != durationState.TypeId)
        {
            throw new ArgumentException("Duration ID and runtime duration state must describe the same duration primitive.", nameof(durationState));
        }

        InstanceId = instanceId;
        SourceId = sourceId;
        TargetId = targetId;
        StatId = statId;
        Operation = operation;
        Value = value;
        Layer = layer;
        Priority = priority;
        DurationId = durationState?.TypeId ?? durationId;
        DurationState = durationState;
    }

    public StableId InstanceId { get; }
    public StableId SourceId { get; }
    public StableId TargetId { get; }
    public StableId StatId { get; }
    public StatModifierOperation Operation { get; }
    public decimal Value { get; }
    public StatModifierLayer Layer { get; }
    public int Priority { get; }
    public StableId? DurationId { get; }
    public StatModifierDurationState? DurationState { get; }
}

public sealed class StatResolution
{
    public StatResolution(decimal baseValue, decimal effectiveValue, IReadOnlyList<StatModifier> appliedModifiers)
    {
        BaseValue = baseValue;
        EffectiveValue = effectiveValue;
        AppliedModifiers = appliedModifiers ?? throw new ArgumentNullException(nameof(appliedModifiers));
    }

    public decimal BaseValue { get; }
    public decimal EffectiveValue { get; }
    public IReadOnlyList<StatModifier> AppliedModifiers { get; }
}

public sealed class StatModifierPipeline
{
    private static readonly StableId CostStatId = StableId.Parse("stat.cost");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private readonly IReadOnlyList<StatModifier> _modifiers;

    public StatModifierPipeline(IEnumerable<StatModifier>? modifiers = null)
    {
        var modifierArray = (modifiers ?? Array.Empty<StatModifier>()).ToArray();
        if (modifierArray.Any(modifier => modifier is null))
        {
            throw new ArgumentException("Modifier collection cannot contain null values.", nameof(modifiers));
        }

        if (modifierArray.GroupBy(modifier => modifier.InstanceId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Modifier instance IDs must be unique.", nameof(modifiers));
        }

        _modifiers = new ReadOnlyCollection<StatModifier>(modifierArray);
    }

    public IReadOnlyList<StatModifier> Modifiers => _modifiers;

    public StatResolution Resolve(RuntimeTarget target, StableId statId)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (statId == default)
        {
            throw new ArgumentException("Stat ID must be a non-default StableId.", nameof(statId));
        }

        var baseValue = target.GetRequiredStat(statId);
        var applicable = _modifiers
            .Where(modifier => modifier.TargetId == target.RuntimeId && modifier.StatId == statId)
            .OrderBy(modifier => modifier.Layer)
            .ThenBy(modifier => modifier.Priority)
            .ThenBy(modifier => modifier.InstanceId)
            .ToArray();
        var effectiveValue = applicable.Aggregate(baseValue, ApplyModifier);
        effectiveValue = ApplyDefaultClamp(statId, effectiveValue);
        return new StatResolution(baseValue, effectiveValue, new ReadOnlyCollection<StatModifier>(applicable));
    }

    private static decimal ApplyModifier(decimal current, StatModifier modifier) =>
        modifier.Operation switch
        {
            StatModifierOperation.Add => current + modifier.Value,
            StatModifierOperation.Multiply => current * modifier.Value,
            StatModifierOperation.Set => modifier.Value,
            StatModifierOperation.Minimum => Math.Max(current, modifier.Value),
            StatModifierOperation.Maximum => Math.Min(current, modifier.Value),
            _ => throw new InvalidOperationException($"Unsupported stat modifier operation '{modifier.Operation}'.")
        };

    private static decimal ApplyDefaultClamp(StableId statId, decimal value)
    {
        if (statId == CostStatId || statId == MaxHealthStatId || statId == DefenseStatId)
        {
            return Math.Max(0m, value);
        }

        return value;
    }
}
