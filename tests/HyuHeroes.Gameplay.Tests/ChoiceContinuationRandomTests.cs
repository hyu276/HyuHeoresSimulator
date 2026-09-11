/**
 * CHOICE_CONTINUATION_RANDOM_TESTS
 * Purpose: Protects transactional player-choice continuation and snapshot-owned deterministic random consumption.
 * Connections: Exercises AbilityResolutionEngine, EffectSequenceEngine, TargetResolver RANDOM_N, MatchStateSnapshot RNG state, and usage/event commits.
 * Risk: High because regressions would create replay divergence, stale-choice acceptance, or partial commits around pending decisions.
 */
using HyuHeroes.Gameplay.Abilities;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;
using HyuHeroes.Gameplay.Simulation;
using HyuHeroes.Gameplay.Triggers;

namespace HyuHeroes.Gameplay.Tests;

public sealed class ChoiceContinuationRandomTests
{
    private static readonly StableId OwnerId = StableId.Parse("player.alpha");
    private static readonly StableId OpponentId = StableId.Parse("player.beta");
    private static readonly StableId SourceId = StableId.Parse("entity.source");
    private static readonly StableId TargetId = StableId.Parse("entity.target");
    private static readonly StableId TargetTwoId = StableId.Parse("entity.target_two");
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId DefenseStatId = StableId.Parse("stat.defense");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId ResourceId = StableId.Parse("resource.primary");

    [Fact]
    public void ResolveChoice_ReplaysPrefixCommitsSelectedTargetAndConsumesUsage()
    {
        var abilityId = StableId.Parse("ability.choice_commit");
        var resource = ChangeResource(2m);
        var heal = new EffectDefinition(EffectIds.Heal, Bag(
            Pair("target", Selector(TargetScope.Unit, TargetRelation.Friendly, TargetZone.Board, TargetSelection.PlayerChoice)),
            Pair("amount", FormulaExpression.Constant(2m))));
        var ability = Ability(abilityId, new[] { resource, heal });
        var binding = Binding(abilityId);
        var queue = Queue(binding);
        var state = CreateState(sourceHealth: 10m, randomState: 41UL);
        var engine = Engine(ability, binding);

        var pending = engine.Resolve(state, queue);

        Assert.True(pending.RequiresPlayerChoice);
        Assert.Equal(5m, pending.State.GetRequiredTarget(OwnerId).GetRequiredResource(ResourceId));
        Assert.Equal(10m, pending.State.GetRequiredTarget(SourceId).CurrentHealth);
        Assert.Equal(41UL, pending.State.RandomState);

        var completed = engine.ResolveChoice(pending.State, queue, pending.PendingChoice!, new[] { SourceId });

        Assert.False(completed.RequiresPlayerChoice);
        Assert.Equal(7m, completed.State.GetRequiredTarget(OwnerId).GetRequiredResource(ResourceId));
        Assert.Equal(12m, completed.State.GetRequiredTarget(SourceId).CurrentHealth);
        Assert.Equal(2, completed.Events.Count);
        Assert.Equal(1, Assert.Single(completed.State.AbilityUsage).TotalResolutions);
    }

    [Fact]
    public void ResolveChoice_RejectsTargetOutsideReplayedCandidates()
    {
        var abilityId = StableId.Parse("ability.choice_validation");
        var heal = new EffectDefinition(EffectIds.Heal, Bag(
            Pair("target", Selector(TargetScope.Unit, TargetRelation.Friendly, TargetZone.Board, TargetSelection.PlayerChoice)),
            Pair("amount", FormulaExpression.Constant(1m))));
        var ability = Ability(abilityId, new[] { heal });
        var binding = Binding(abilityId);
        var queue = Queue(binding);
        var state = CreateState();
        var engine = Engine(ability, binding);
        var pending = engine.Resolve(state, queue);

        Assert.Throws<InvalidOperationException>(() =>
            engine.ResolveChoice(pending.State, queue, pending.PendingChoice!, new[] { TargetId }));
    }

    [Fact]
    public void ResolveChoice_RejectsStaleSnapshotCheckpoint()
    {
        var abilityId = StableId.Parse("ability.choice_checkpoint");
        var heal = new EffectDefinition(EffectIds.Heal, Bag(
            Pair("target", Selector(TargetScope.Unit, TargetRelation.Friendly, TargetZone.Board, TargetSelection.PlayerChoice)),
            Pair("amount", FormulaExpression.Constant(1m))));
        var ability = Ability(abilityId, new[] { heal });
        var binding = Binding(abilityId);
        var queue = Queue(binding);
        var engine = Engine(ability, binding);
        var pending = engine.Resolve(CreateState(randomState: 9UL), queue);
        var stale = pending.State.With(nextEventSequence: pending.State.NextEventSequence + 1);

        Assert.Throws<InvalidOperationException>(() =>
            engine.ResolveChoice(stale, queue, pending.PendingChoice!, new[] { SourceId }));
    }

    [Fact]
    public void RandomN_SameSnapshotStateProducesSameOutcomeAndAdvancesState()
    {
        var abilityId = StableId.Parse("ability.random_strike");
        var damage = RandomEnemyDamage(3m);
        var ability = Ability(abilityId, new[] { damage });
        var binding = Binding(abilityId);
        var first = ResolveOnce(CreateState(randomState: 123456UL), ability, binding);
        var second = ResolveOnce(CreateState(randomState: 123456UL), ability, binding);

        Assert.Equal(first.State.RandomState, second.State.RandomState);
        Assert.NotEqual(123456UL, first.State.RandomState);
        Assert.Equal(first.State.GetRequiredTarget(TargetId).CurrentHealth, second.State.GetRequiredTarget(TargetId).CurrentHealth);
        Assert.Equal(first.State.GetRequiredTarget(TargetTwoId).CurrentHealth, second.State.GetRequiredTarget(TargetTwoId).CurrentHealth);
        Assert.Equal(37m, first.State.GetRequiredTarget(TargetId).CurrentHealth + first.State.GetRequiredTarget(TargetTwoId).CurrentHealth);
    }

    [Fact]
    public void PendingChoice_DoesNotCommitRandomConsumptionUntilContinuation()
    {
        var abilityId = StableId.Parse("ability.random_then_choice");
        var randomDamage = RandomEnemyDamage(1m);
        var healChoice = new EffectDefinition(EffectIds.Heal, Bag(
            Pair("target", Selector(TargetScope.Unit, TargetRelation.Friendly, TargetZone.Board, TargetSelection.PlayerChoice)),
            Pair("amount", FormulaExpression.Constant(1m))));
        var ability = Ability(abilityId, new[] { randomDamage, healChoice });
        var binding = Binding(abilityId);
        var queue = Queue(binding);
        var state = CreateState(sourceHealth: 10m, randomState: 777UL);
        var engine = Engine(ability, binding);

        var pending = engine.Resolve(state, queue);

        Assert.Equal(777UL, pending.State.RandomState);
        Assert.Equal(20m, pending.State.GetRequiredTarget(TargetId).CurrentHealth);
        Assert.Equal(20m, pending.State.GetRequiredTarget(TargetTwoId).CurrentHealth);

        var completed = engine.ResolveChoice(pending.State, queue, pending.PendingChoice!, new[] { SourceId });

        Assert.NotEqual(777UL, completed.State.RandomState);
        Assert.Equal(11m, completed.State.GetRequiredTarget(SourceId).CurrentHealth);
        Assert.Equal(39m, completed.State.GetRequiredTarget(TargetId).CurrentHealth + completed.State.GetRequiredTarget(TargetTwoId).CurrentHealth);
    }

    private static AbilityResolutionResult ResolveOnce(
        MatchStateSnapshot state,
        AbilityDefinition ability,
        TriggerBinding binding)
    {
        var queue = Queue(binding);
        return Engine(ability, binding).Resolve(state, queue);
    }

    private static AbilityResolutionEngine Engine(AbilityDefinition ability, TriggerBinding binding) =>
        new(new AbilityRuntimeCatalog(new[] { ability }), new TriggerDiscovery(new[] { binding }));

    private static AbilityDefinition Ability(StableId abilityId, IEnumerable<EffectDefinition> effects) =>
        new(
            new GameplayDefinitionHeader(abilityId, 1, 1, ContentStatus.Published, "loc." + abilityId.Value.Replace('.', '_')),
            new TriggerSpec(TriggerIds.OnDeath),
            effects,
            usageLimit: new UsageLimitSpec(UsageLimitIds.OncePerMatch));

    private static TriggerBinding Binding(StableId abilityId) =>
        new(StableId.Parse("binding." + abilityId.Value.Replace("ability.", string.Empty)), SourceId, abilityId, TriggerIds.OnDeath);

    private static DeterministicTriggerQueue Queue(TriggerBinding binding)
    {
        var queue = new DeterministicTriggerQueue();
        queue.Enqueue(new TriggerQueueItem(new EntityDiedDomainEvent(1, SourceId), 0, binding));
        return queue;
    }

    private static EffectDefinition RandomEnemyDamage(decimal amount) =>
        new(EffectIds.Damage, Bag(
            Pair("target", Selector(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board, TargetSelection.RandomN)),
            Pair("amount", FormulaExpression.Constant(amount)),
            Pair("damageType", new EnumParameterValue("TRUE"))));

    private static EffectDefinition ChangeResource(decimal amount) =>
        new(EffectIds.ChangeResource, Bag(
            Pair("target", Selector(TargetScope.Owner)),
            Pair("resourceId", new StableIdParameterValue(ResourceId)),
            Pair("amount", FormulaExpression.Constant(amount))));

    private static ParameterBag Bag(params KeyValuePair<string, ParameterValue>[] values) => new(values);

    private static KeyValuePair<string, ParameterValue> Pair(string key, ParameterValue value) => new(key, value);

    private static KeyValuePair<string, ParameterValue> Pair(string key, FormulaExpression value) =>
        Pair(key, new FormulaParameterValue(value));

    private static SelectorParameterValue Selector(
        TargetScope scope,
        TargetRelation relation = TargetRelation.Any,
        TargetZone zone = TargetZone.Any,
        TargetSelection selection = TargetSelection.All) =>
        new(new TargetSelectorSpec(scope, relation, zone, selection: selection));

    private static MatchStateSnapshot CreateState(
        decimal sourceHealth = 20m,
        ulong randomState = 0UL)
    {
        var targets = new[]
        {
            new RuntimeTarget(OwnerId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 5m) }),
            new RuntimeTarget(OpponentId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 5m) }),
            new RuntimeTarget(StableId.Parse("lane.left"), RuntimeTargetKind.Lane, laneIndex: 0),
            new RuntimeTarget(StableId.Parse("lane.right"), RuntimeTargetKind.Lane, laneIndex: 1),
            Unit(SourceId, OwnerId, 3m, 20m, 0, 0, sourceHealth),
            Unit(TargetId, OpponentId, 2m, 20m, 0, 0, 20m),
            Unit(TargetTwoId, OpponentId, 2m, 20m, 0, 1, 20m)
        };
        return new MatchStateSnapshot(targets, 3, StableId.Parse("phase.main_action"), 2, randomState: randomState);
    }

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
