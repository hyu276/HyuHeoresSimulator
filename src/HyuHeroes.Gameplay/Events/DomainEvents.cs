/**
 * DOMAIN_EVENTS
 * Purpose: Records immutable evidence of authoritative simulation changes for replay, trigger discovery, and presentation synchronization.
 * Connections: Emitted by StateTransitionEngine and consumed by TriggerDiscovery plus future replay, networking, and UI adapters.
 * Risk: High because event identity and ordering form the observable history of authoritative gameplay resolution.
 */
using System;
using HyuHeroes.Gameplay.Combat;
using HyuHeroes.Gameplay.Core;

namespace HyuHeroes.Gameplay.Events;

public static class DomainEventTypeIds
{
    public static readonly StableId DamageApplied = StableId.Parse("event.damage_applied");
    public static readonly StableId Healed = StableId.Parse("event.healed");
    public static readonly StableId ResourceChanged = StableId.Parse("event.resource_changed");
    public static readonly StableId StatSet = StableId.Parse("event.stat_set");
    public static readonly StableId ModifierAdded = StableId.Parse("event.modifier_added");
    public static readonly StableId EntityDied = StableId.Parse("event.entity_died");
}

public abstract class DomainEvent
{
    protected DomainEvent(long sequence, StableId typeId, StableId sourceId, StableId? targetId = null)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Domain event sequence must be positive.");
        }

        if (typeId == default || sourceId == default)
        {
            throw new ArgumentException("Domain event type and source IDs must be non-default StableIds.");
        }

        if (targetId is { } concreteTargetId && concreteTargetId == default)
        {
            throw new ArgumentException("Domain event target ID must be null or a non-default StableId.", nameof(targetId));
        }

        Sequence = sequence;
        TypeId = typeId;
        SourceId = sourceId;
        TargetId = targetId;
    }

    public long Sequence { get; }
    public StableId TypeId { get; }
    public StableId SourceId { get; }
    public StableId? TargetId { get; }
}

public sealed class DamageAppliedDomainEvent : DomainEvent
{
    public DamageAppliedDomainEvent(long sequence, StableId sourceId, StableId targetId, DamageResolution resolution)
        : base(sequence, DomainEventTypeIds.DamageApplied, sourceId, targetId)
    {
        Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
    }

    public DamageResolution Resolution { get; }
}

public sealed class HealedDomainEvent : DomainEvent
{
    public HealedDomainEvent(
        long sequence,
        StableId sourceId,
        StableId targetId,
        decimal requestedAmount,
        decimal healthGained,
        decimal resultingHealth)
        : base(sequence, DomainEventTypeIds.Healed, sourceId, targetId)
    {
        RequestedAmount = requestedAmount;
        HealthGained = healthGained;
        ResultingHealth = resultingHealth;
    }

    public decimal RequestedAmount { get; }
    public decimal HealthGained { get; }
    public decimal ResultingHealth { get; }
}

public sealed class ResourceChangedDomainEvent : DomainEvent
{
    public ResourceChangedDomainEvent(
        long sequence,
        StableId sourceId,
        StableId targetId,
        StableId resourceId,
        decimal requestedDelta,
        decimal resultingValue)
        : base(sequence, DomainEventTypeIds.ResourceChanged, sourceId, targetId)
    {
        ResourceId = resourceId == default
            ? throw new ArgumentException("Resource ID must be a non-default StableId.", nameof(resourceId))
            : resourceId;
        RequestedDelta = requestedDelta;
        ResultingValue = resultingValue;
    }

    public StableId ResourceId { get; }
    public decimal RequestedDelta { get; }
    public decimal ResultingValue { get; }
}

public sealed class StatSetDomainEvent : DomainEvent
{
    public StatSetDomainEvent(
        long sequence,
        StableId sourceId,
        StableId targetId,
        StableId statId,
        decimal previousBaseValue,
        decimal resultingBaseValue)
        : base(sequence, DomainEventTypeIds.StatSet, sourceId, targetId)
    {
        StatId = statId == default
            ? throw new ArgumentException("Stat ID must be a non-default StableId.", nameof(statId))
            : statId;
        PreviousBaseValue = previousBaseValue;
        ResultingBaseValue = resultingBaseValue;
    }

    public StableId StatId { get; }
    public decimal PreviousBaseValue { get; }
    public decimal ResultingBaseValue { get; }
}

public sealed class ModifierAddedDomainEvent : DomainEvent
{
    public ModifierAddedDomainEvent(
        long sequence,
        StableId sourceId,
        StableId targetId,
        StableId modifierInstanceId,
        StableId statId,
        decimal value)
        : base(sequence, DomainEventTypeIds.ModifierAdded, sourceId, targetId)
    {
        ModifierInstanceId = modifierInstanceId;
        StatId = statId;
        Value = value;
    }

    public StableId ModifierInstanceId { get; }
    public StableId StatId { get; }
    public decimal Value { get; }
}

public sealed class EntityDiedDomainEvent : DomainEvent
{
    public EntityDiedDomainEvent(long sequence, StableId entityId)
        : base(sequence, DomainEventTypeIds.EntityDied, entityId)
    {
    }
}
