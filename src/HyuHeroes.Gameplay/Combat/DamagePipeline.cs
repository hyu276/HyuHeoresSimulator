/**
 * CANONICAL_DAMAGE_PIPELINE
 * Purpose: Resolves every damage request through one deterministic sequence of adjustments, defense, prevention, and health-loss calculation.
 * Connections: Consumes resolved DAMAGE operations, shared effective stats, and feeds the future reducer, domain-event stream, and death checks.
 * Risk: High because damage ordering is authoritative combat math and must remain globally consistent for balance and replay determinism.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Runtime;

namespace HyuHeroes.Gameplay.Combat;

public enum DamageType
{
    Physical,
    Arcane,
    True
}

public enum DamageAdjustmentStage
{
    Source,
    DamageType,
    Target
}

public enum DamageAdjustmentOperation
{
    Add,
    Multiply,
    Set,
    Minimum,
    Maximum
}

public sealed class DamageAdjustment
{
    public DamageAdjustment(
        StableId instanceId,
        DamageAdjustmentStage stage,
        DamageAdjustmentOperation operation,
        decimal value,
        int priority = 0,
        StableId? sourceId = null,
        StableId? targetId = null,
        DamageType? damageType = null)
    {
        if (instanceId == default)
        {
            throw new ArgumentException("Damage adjustment ID must be a non-default StableId.", nameof(instanceId));
        }

        if (!Enum.IsDefined(typeof(DamageAdjustmentStage), stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Damage adjustment stage is not defined.");
        }

        if (!Enum.IsDefined(typeof(DamageAdjustmentOperation), operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Damage adjustment operation is not defined.");
        }

        ValidateOptionalId(sourceId, nameof(sourceId));
        ValidateOptionalId(targetId, nameof(targetId));
        ValidateStageSubject(stage, sourceId, targetId, damageType);
        InstanceId = instanceId;
        Stage = stage;
        Operation = operation;
        Value = value;
        Priority = priority;
        SourceId = sourceId;
        TargetId = targetId;
        DamageType = damageType;
    }

    public StableId InstanceId { get; }
    public DamageAdjustmentStage Stage { get; }
    public DamageAdjustmentOperation Operation { get; }
    public decimal Value { get; }
    public int Priority { get; }
    public StableId? SourceId { get; }
    public StableId? TargetId { get; }
    public DamageType? DamageType { get; }

    private static void ValidateStageSubject(
        DamageAdjustmentStage stage,
        StableId? sourceId,
        StableId? targetId,
        DamageType? damageType)
    {
        if (stage == DamageAdjustmentStage.Source && sourceId is null)
        {
            throw new ArgumentException("Source-stage damage adjustments require a source ID.", nameof(sourceId));
        }

        if (stage == DamageAdjustmentStage.Target && targetId is null)
        {
            throw new ArgumentException("Target-stage damage adjustments require a target ID.", nameof(targetId));
        }

        if (stage == DamageAdjustmentStage.DamageType && damageType is null)
        {
            throw new ArgumentException("Damage-type adjustments require a damage type.", nameof(damageType));
        }
    }

    private static void ValidateOptionalId(StableId? value, string parameterName)
    {
        if (value is { } concreteId && concreteId == default)
        {
            throw new ArgumentException("Optional damage adjustment IDs must be null or non-default StableIds.", parameterName);
        }
    }
}

public sealed class DamagePrevention
{
    public DamagePrevention(
        StableId instanceId,
        StableId targetId,
        decimal capacity,
        int priority = 0,
        DamageType? damageType = null)
    {
        if (instanceId == default || targetId == default)
        {
            throw new ArgumentException("Damage prevention identity and target IDs must be non-default StableIds.");
        }

        if (capacity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Damage prevention capacity cannot be negative.");
        }

        InstanceId = instanceId;
        TargetId = targetId;
        Capacity = capacity;
        Priority = priority;
        DamageType = damageType;
    }

    public StableId InstanceId { get; }
    public StableId TargetId { get; }
    public decimal Capacity { get; }
    public int Priority { get; }
    public DamageType? DamageType { get; }
}

public sealed class AppliedDamagePrevention
{
    public AppliedDamagePrevention(StableId preventionId, decimal preventedAmount)
    {
        PreventionId = preventionId;
        PreventedAmount = preventedAmount;
    }

    public StableId PreventionId { get; }
    public decimal PreventedAmount { get; }
}

public sealed class DamageResolution
{
    public DamageResolution(
        DamageType damageType,
        decimal requestedAmount,
        decimal baseDamage,
        decimal afterSourceAdjustments,
        decimal afterTypeAdjustments,
        decimal afterTargetAdjustments,
        decimal defensePrevented,
        decimal preventionPrevented,
        decimal finalDamage,
        decimal previousHealth,
        decimal healthLost,
        decimal resultingHealth,
        decimal overkill,
        IReadOnlyList<DamageAdjustment> appliedAdjustments,
        IReadOnlyList<AppliedDamagePrevention> appliedPreventions)
    {
        DamageType = damageType;
        RequestedAmount = requestedAmount;
        BaseDamage = baseDamage;
        AfterSourceAdjustments = afterSourceAdjustments;
        AfterTypeAdjustments = afterTypeAdjustments;
        AfterTargetAdjustments = afterTargetAdjustments;
        DefensePrevented = defensePrevented;
        PreventionPrevented = preventionPrevented;
        FinalDamage = finalDamage;
        PreviousHealth = previousHealth;
        HealthLost = healthLost;
        ResultingHealth = resultingHealth;
        Overkill = overkill;
        AppliedAdjustments = appliedAdjustments;
        AppliedPreventions = appliedPreventions;
    }

    public DamageType DamageType { get; }
    public decimal RequestedAmount { get; }
    public decimal BaseDamage { get; }
    public decimal AfterSourceAdjustments { get; }
    public decimal AfterTypeAdjustments { get; }
    public decimal AfterTargetAdjustments { get; }
    public decimal DefensePrevented { get; }
    public decimal PreventionPrevented { get; }
    public decimal FinalDamage { get; }
    public decimal PreviousHealth { get; }
    public decimal HealthLost { get; }
    public decimal ResultingHealth { get; }
    public decimal Overkill { get; }
    public bool WouldDie => PreviousHealth > 0m && ResultingHealth <= 0m;
    public IReadOnlyList<DamageAdjustment> AppliedAdjustments { get; }
    public IReadOnlyList<AppliedDamagePrevention> AppliedPreventions { get; }
}

public sealed class DamagePipeline
{
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private readonly IReadOnlyList<DamageAdjustment> _adjustments;
    private readonly GameplayRuntimeEvaluator _evaluator;
    private readonly IReadOnlyList<DamagePrevention> _preventions;

    public DamagePipeline(
        GameplayRuntimeEvaluator evaluator,
        IEnumerable<DamageAdjustment>? adjustments = null,
        IEnumerable<DamagePrevention>? preventions = null)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _adjustments = CopyUnique(adjustments, item => item.InstanceId, nameof(adjustments));
        _preventions = CopyUnique(preventions, item => item.InstanceId, nameof(preventions));
    }

    public DamageResolution Resolve(ResolvedEffectOperation operation, GameplayRuntimeContext runtime)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        if (operation.Kind != ResolvedEffectOperationKind.DamageRequest)
        {
            throw new ArgumentException("Canonical damage resolution requires a DamageRequest operation.", nameof(operation));
        }

        var damageType = ParseDamageType(operation.Qualifier);
        var source = runtime.GetRequiredTarget(operation.SourceId);
        var target = runtime.GetRequiredTarget(operation.TargetId);
        var appliedAdjustments = new List<DamageAdjustment>();
        var baseDamage = Math.Max(0m, operation.Amount);
        var afterSource = ApplyAdjustments(baseDamage, DamageAdjustmentStage.Source, source.RuntimeId, target.RuntimeId, damageType, appliedAdjustments);
        var afterType = ApplyAdjustments(afterSource, DamageAdjustmentStage.DamageType, source.RuntimeId, target.RuntimeId, damageType, appliedAdjustments);
        var afterTarget = ApplyAdjustments(afterType, DamageAdjustmentStage.Target, source.RuntimeId, target.RuntimeId, damageType, appliedAdjustments);
        var afterDefense = ApplyDefense(afterTarget, target, damageType);
        var defensePrevented = afterTarget - afterDefense;
        var preventionResult = ApplyPrevention(afterDefense, target.RuntimeId, damageType);
        var finalDamage = Math.Max(0m, preventionResult.RemainingDamage);
        var previousHealth = GetRequiredCurrentHealth(target);
        var healthLost = Math.Min(previousHealth, finalDamage);
        var resultingHealth = Math.Max(0m, previousHealth - healthLost);
        var overkill = Math.Max(0m, finalDamage - healthLost);

        return new DamageResolution(
            damageType,
            operation.Amount,
            baseDamage,
            afterSource,
            afterType,
            afterTarget,
            defensePrevented,
            preventionResult.PreventedDamage,
            finalDamage,
            previousHealth,
            healthLost,
            resultingHealth,
            overkill,
            new ReadOnlyCollection<DamageAdjustment>(appliedAdjustments),
            preventionResult.AppliedPreventions);
    }

    private decimal ApplyAdjustments(
        decimal input,
        DamageAdjustmentStage stage,
        StableId sourceId,
        StableId targetId,
        DamageType damageType,
        ICollection<DamageAdjustment> applied)
    {
        var relevant = _adjustments
            .Where(adjustment => MatchesAdjustment(adjustment, stage, sourceId, targetId, damageType))
            .OrderBy(adjustment => adjustment.Priority)
            .ThenBy(adjustment => adjustment.InstanceId);
        var value = input;
        foreach (var adjustment in relevant)
        {
            value = ApplyOperation(value, adjustment.Operation, adjustment.Value);
            applied.Add(adjustment);
        }

        return Math.Max(0m, value);
    }

    private decimal ApplyDefense(decimal damage, RuntimeTarget target, DamageType damageType)
    {
        if (damageType != DamageType.Physical || !target.TryGetStat(DefenseStatId, out _))
        {
            return damage;
        }

        var defense = _evaluator.ResolveStat(target, DefenseStatId);
        return Math.Max(0m, damage - defense);
    }

    private PreventionResult ApplyPrevention(decimal damage, StableId targetId, DamageType damageType)
    {
        var remaining = damage;
        var applied = new List<AppliedDamagePrevention>();
        var relevant = _preventions
            .Where(prevention => prevention.TargetId == targetId && MatchesDamageType(prevention.DamageType, damageType))
            .OrderBy(prevention => prevention.Priority)
            .ThenBy(prevention => prevention.InstanceId);
        foreach (var prevention in relevant)
        {
            var prevented = Math.Min(remaining, prevention.Capacity);
            if (prevented > 0m)
            {
                applied.Add(new AppliedDamagePrevention(prevention.InstanceId, prevented));
                remaining -= prevented;
            }
        }

        return new PreventionResult(
            remaining,
            damage - remaining,
            new ReadOnlyCollection<AppliedDamagePrevention>(applied));
    }

    private decimal GetRequiredCurrentHealth(RuntimeTarget target)
    {
        if (!target.TryGetStat(MaxHealthStatId, out _))
        {
            throw new InvalidOperationException($"Damage target '{target.RuntimeId}' does not expose maximum health.");
        }

        var currentHealth = target.CurrentHealth ?? _evaluator.ResolveStat(target, MaxHealthStatId);
        if (currentHealth < 0m)
        {
            throw new InvalidOperationException($"Damage target '{target.RuntimeId}' exposes negative current health.");
        }

        return currentHealth;
    }

    private static bool MatchesAdjustment(
        DamageAdjustment adjustment,
        DamageAdjustmentStage stage,
        StableId sourceId,
        StableId targetId,
        DamageType damageType)
    {
        if (adjustment.Stage != stage || !MatchesDamageType(adjustment.DamageType, damageType))
        {
            return false;
        }

        return stage switch
        {
            DamageAdjustmentStage.Source => adjustment.SourceId == sourceId,
            DamageAdjustmentStage.DamageType => adjustment.DamageType == damageType,
            DamageAdjustmentStage.Target => adjustment.TargetId == targetId,
            _ => false
        };
    }

    private static bool MatchesDamageType(DamageType? configuredType, DamageType actualType) =>
        configuredType is null || configuredType == actualType;

    private static decimal ApplyOperation(decimal current, DamageAdjustmentOperation operation, decimal value) =>
        operation switch
        {
            DamageAdjustmentOperation.Add => current + value,
            DamageAdjustmentOperation.Multiply => current * value,
            DamageAdjustmentOperation.Set => value,
            DamageAdjustmentOperation.Minimum => Math.Max(current, value),
            DamageAdjustmentOperation.Maximum => Math.Min(current, value),
            _ => throw new InvalidOperationException($"Unsupported damage adjustment operation '{operation}'.")
        };

    private static DamageType ParseDamageType(string? value) =>
        value switch
        {
            "PHYSICAL" => DamageType.Physical,
            "ARCANE" => DamageType.Arcane,
            "TRUE" => DamageType.True,
            _ => throw new InvalidOperationException($"Unsupported damage type '{value ?? "<null>"}'.")
        };

    private static IReadOnlyList<TItem> CopyUnique<TItem>(
        IEnumerable<TItem>? values,
        Func<TItem, StableId> idSelector,
        string parameterName)
        where TItem : class
    {
        var items = (values ?? Array.Empty<TItem>()).ToArray();
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Damage pipeline collections cannot contain null values.", parameterName);
        }

        if (items.GroupBy(idSelector).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Damage pipeline instance IDs must be unique within each collection.", parameterName);
        }

        return new ReadOnlyCollection<TItem>(items);
    }

    private sealed class PreventionResult
    {
        public PreventionResult(
            decimal remainingDamage,
            decimal preventedDamage,
            IReadOnlyList<AppliedDamagePrevention> appliedPreventions)
        {
            RemainingDamage = remainingDamage;
            PreventedDamage = preventedDamage;
            AppliedPreventions = appliedPreventions;
        }

        public decimal RemainingDamage { get; }
        public decimal PreventedDamage { get; }
        public IReadOnlyList<AppliedDamagePrevention> AppliedPreventions { get; }
    }
}
