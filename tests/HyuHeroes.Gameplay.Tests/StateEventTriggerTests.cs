/**
 * STATE_EVENT_TRIGGER_TESTS
 * Purpose: Verifies immutable state mutation, ordered domain events, state-based death, shield consumption, and deterministic trigger discovery.
 * Connections: Exercises StateTransitionEngine, DamagePipeline, DomainEvents, TriggerDiscovery, TriggerQueue, and runtime snapshots end-to-end.
 * Risk: High because these tests protect the authoritative transition and reaction ordering required for replay-safe gameplay.
 */
using HyuHeroes.Gameplay.Combat;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;
using HyuHeroes.Gameplay.Simulation;
using HyuHeroes.Gameplay.Triggers;

namespace HyuHeroes.Gameplay.Tests;

public sealed class StateEventTriggerTests
{
    private static readonly StableId OwnerId = StableId.Parse("player.alpha");
    private static readonly StableId OpponentId = StableId.Parse("player.beta");
    private static readonly StableId SourceId = StableId.Parse("entity.source");
    private static readonly StableId TargetId = StableId.Parse("entity.target");
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId ResourceId = StableId.Parse("resource.primary");

    [Fact]
    public void DamageTransition_MutatesNewStateConsumesShieldAndEmitsDeathAfterDamage()
    {
        var original = CreateState(new[]
        {
            new DamagePrevention(StableId.Parse("prevention.shield"), TargetId, 2m)
        });
        var result = new StateTransitionEngine().Apply(original, DamageOperation(10m));

        Assert.Equal(4m, original.GetRequiredTarget(TargetId).CurrentHealth);
        Assert.Equal(TargetZone.Board, original.GetRequiredTarget(TargetId).Zone);
        var updatedTarget = result.State.GetRequiredTarget(TargetId);
        Assert.Equal(0m, updatedTarget.CurrentHealth);
        Assert.Equal(TargetZone.Graveyard, updatedTarget.Zone);
        Assert.Null(updatedTarget.LaneIndex);
        Assert.Empty(result.State.DamagePreventions);
        Assert.Collection(result.Events, item => Assert.IsType<DamageAppliedDomainEvent>(item), item => Assert.IsType<EntityDiedDomainEvent>(item));
        Assert.Equal(new long[] { 1, 2 }, result.Events.Select(item => item.Sequence));
        Assert.Equal(3, result.State.NextEventSequence);
    }

    [Fact]
    public void HealTransition_CapsAtEffectiveMaximumHealth()
    {
        var state = CreateState(targetHealth: 2m);
        var operation = new ResolvedEffectOperation(ResolvedEffectOperationKind.Heal, EffectIds.Heal, SourceId, TargetId, 5m);

        var result = new StateTransitionEngine().Apply(state, operation);

        var healed = Assert.IsType<HealedDomainEvent>(Assert.Single(result.Events));
        Assert.Equal(2m, healed.HealthGained);
        Assert.Equal(4m, result.State.GetRequiredTarget(TargetId).CurrentHealth);
    }

    [Fact]
    public void ResourceTransition_ClampsAtZeroAndEmitsResultingValue()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.ChangeResource,
            EffectIds.ChangeResource,
            SourceId,
            OwnerId,
            -9m,
            ResourceId);

        var result = new StateTransitionEngine().Apply(state, operation);

        var changed = Assert.IsType<ResourceChangedDomainEvent>(Assert.Single(result.Events));
        Assert.Equal(0m, changed.ResultingValue);
        Assert.Equal(0m, result.State.GetRequiredTarget(OwnerId).GetRequiredResource(ResourceId));
    }

    [Fact]
    public void ModifierTransition_CreatesDeterministicRuntimeModifierIdentity()
    {
        var state = CreateState();
        var operation = new ResolvedEffectOperation(
            ResolvedEffectOperationKind.AddStatModifier,
            EffectIds.ModifyStat,
            SourceId,
            SourceId,
            2m,
            AttackStatId,
            StableId.Parse("duration.until_end_of_turn"),
            "ADD");

        var result = new StateTransitionEngine().Apply(state, operation);

        var modifier = Assert.Single(result.State.StatModifiers);
        Assert.Equal("modifier.runtime_00000001", modifier.InstanceId.Value);
        Assert.Equal(StatModifierLayer.Temporary, modifier.Layer);
        var added = Assert.IsType<ModifierAddedDomainEvent>(Assert.Single(result.Events));
        Assert.Equal(modifier.InstanceId, added.ModifierInstanceId);
    }

    [Fact]
    public void TriggerDiscovery_OrdersDamageDealtBeforeDamagedThenDeathAndPreservesEventContext()
    {
        var dealerBinding = Binding("binding.dealer", SourceId, "ability.dealer", TriggerIds.OnDamageDealt);
        var damagedBinding = Binding("binding.damaged", TargetId, "ability.damaged", TriggerIds.OnDamaged);
        var deathBinding = Binding("binding.death", TargetId, "ability.death", TriggerIds.OnDeath);
        var transition = new StateTransitionEngine().Apply(CreateState(), DamageOperation(10m));
        var damageEvent = Assert.IsType<DamageAppliedDomainEvent>(transition.Events[0]);
        var queue = new DeterministicTriggerQueue();

        new TriggerDiscovery(new[] { deathBinding, damagedBinding, dealerBinding }).DiscoverInto(transition.Events, queue);

        var queued = queue.Snapshot();
        Assert.Equal(new[] { "binding.dealer", "binding.damaged", "binding.death" }, queued.Select(item => item.Binding.BindingId.Value));
        Assert.Equal(new long[] { 1, 1, 2 }, queued.Select(item => item.EventSequence));
        Assert.Same(damageEvent, queued[0].OriginatingEvent);
        Assert.Equal(TargetId, queued[0].OriginatingEvent.TargetId);
    }

    [Fact]
    public void TriggerDiscovery_FullyPreventedDamageDoesNotOpenDamageWindows()
    {
        var dealerBinding = Binding("binding.dealer", SourceId, "ability.dealer", TriggerIds.OnDamageDealt);
        var damagedBinding = Binding("binding.damaged", TargetId, "ability.damaged", TriggerIds.OnDamaged);
        var state = CreateState(new[]
        {
            new DamagePrevention(StableId.Parse("prevention.full_shield"), TargetId, 10m)
        });
        var transition = new StateTransitionEngine().Apply(state, DamageOperation(5m));
        var damageEvent = Assert.IsType<DamageAppliedDomainEvent>(Assert.Single(transition.Events));

        var discovered = new TriggerDiscovery(new[] { dealerBinding, damagedBinding }).Discover(damageEvent);

        Assert.Equal(0m, damageEvent.Resolution.HealthLost);
        Assert.Empty(discovered);
    }

    [Fact]
    public void TriggerQueue_UsesHigherPriorityBeforeStableTieBreakers()
    {
        var domainEvent = new EntityDiedDomainEvent(4, TargetId);
        var low = new TriggerQueueItem(domainEvent, 1, Binding("binding.low", TargetId, "ability.low", TriggerIds.OnDamaged, 1));
        var high = new TriggerQueueItem(domainEvent, 1, Binding("binding.high", TargetId, "ability.high", TriggerIds.OnDamaged, 10));
        var queue = new DeterministicTriggerQueue();

        queue.Enqueue(low);
        queue.Enqueue(high);

        Assert.Equal("binding.high", queue.Dequeue().Binding.BindingId.Value);
        Assert.Equal("binding.low", queue.Dequeue().Binding.BindingId.Value);
    }

    private static TriggerBinding Binding(string bindingId, StableId sourceId, string abilityId, StableId triggerId, int priority = 0) =>
        new(StableId.Parse(bindingId), sourceId, StableId.Parse(abilityId), triggerId, priority);

    private static ResolvedEffectOperation DamageOperation(decimal amount) =>
        new(ResolvedEffectOperationKind.DamageRequest, EffectIds.Damage, SourceId, TargetId, amount, qualifier: "PHYSICAL");

    private static MatchStateSnapshot CreateState(IEnumerable<DamagePrevention>? preventions = null, decimal targetHealth = 4m)
    {
        var targets = new[]
        {
            Player(OwnerId, 5m),
            Player(OpponentId, 4m),
            new RuntimeTarget(StableId.Parse("lane.left"), RuntimeTargetKind.Lane, laneIndex: 0),
            Unit(SourceId, OwnerId, 3m, 5m, 0m, 0, 5m),
            Unit(TargetId, OpponentId, 2m, 4m, 3m, 0, targetHealth)
        };
        return new MatchStateSnapshot(
            targets,
            turnNumber: 3,
            phaseId: StableId.Parse("phase.main_action"),
            laneCount: 1,
            damagePreventions: preventions);
    }

    private static RuntimeTarget Player(StableId id, decimal resource) =>
        new(id, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, resource) });

    private static RuntimeTarget Unit(
        StableId id,
        StableId ownerId,
        decimal attack,
        decimal maxHealth,
        decimal defense,
        int laneIndex,
        decimal currentHealth) =>
        new(
            id,
            RuntimeTargetKind.Unit,
            ownerId,
            TargetZone.Board,
            laneIndex,
            CardType.Unit,
            stats: new[]
            {
                new KeyValuePair<StableId, decimal>(AttackStatId, attack),
                new KeyValuePair<StableId, decimal>(MaxHealthStatId, maxHealth),
                new KeyValuePair<StableId, decimal>(DefenseStatId, defense)
            },
            currentHealth: currentHealth);
}
