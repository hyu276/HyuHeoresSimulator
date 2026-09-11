/**
 * ABILITY_RESOLUTION_ENGINE_TESTS
 * Purpose: Protects sequential triggered ability execution, usage limits, transactional player choice, event chaining, and loop safeguards.
 * Connections: Exercises AbilityResolutionEngine, EffectSequenceEngine, TriggerQueue, StateTransitionEngine, schema abilities, and usage ledger state.
 * Risk: High because regressions would make chained gameplay order, replay outcomes, or trigger eligibility nondeterministic.
 */
using HyuHeroes.Gameplay.Abilities;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
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

public sealed class AbilityResolutionEngineTests
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
    public void Resolve_ExecutesLaterEffectsAgainstStateProducedByEarlierEffects()
    {
        var abilityId = StableId.Parse("ability.combo_strike");
        var setAttack = new EffectDefinition(EffectIds.SetStat, Bag(
            Pair("target", Selector(TargetScope.Source)),
            Pair("statId", new StableIdParameterValue(AttackStatId)),
            Pair("value", FormulaExpression.Constant(10m))));
        var damage = new EffectDefinition(EffectIds.Damage, Bag(
            Pair("target", Selector(TargetScope.Unit, TargetRelation.Enemy, TargetZone.Board)),
            Pair("amount", FormulaExpression.Variable(StableId.Parse("variable.source.attack"))),
            Pair("damageType", new EnumParameterValue("PHYSICAL"))));
        var ability = Ability(abilityId, TriggerIds.OnDamageDealt, new[] { setAttack, damage });
        var binding = Binding("binding.combo", SourceId, abilityId, TriggerIds.OnDamageDealt);
        var transition = new StateTransitionEngine().Apply(CreateState(targetHealth: 20m), DamageOperation(1m));
        var queue = QueueFromEvents(new[] { binding }, transition.Events);
        var engine = Engine(new[] { ability }, new[] { binding });

        var result = engine.Resolve(transition.State, queue);

        Assert.False(result.RequiresPlayerChoice);
        Assert.Equal(10m, result.State.GetRequiredTarget(SourceId).GetRequiredStat(AttackStatId));
        Assert.Equal(9m, result.State.GetRequiredTarget(TargetId).CurrentHealth);
        Assert.Equal(2, result.Events.Count);
        Assert.Single(result.State.AbilityUsage);
    }

    [Fact]
    public void Resolve_OncePerMatchSkipsLaterQueuedOccurrences()
    {
        var abilityId = StableId.Parse("ability.once_reward");
        var effect = new EffectDefinition(EffectIds.ChangeResource, Bag(
            Pair("target", Selector(TargetScope.Owner)),
            Pair("resourceId", new StableIdParameterValue(ResourceId)),
            Pair("amount", FormulaExpression.Constant(1m))));
        var limit = new UsageLimitSpec(UsageLimitIds.OncePerMatch);
        var ability = Ability(abilityId, TriggerIds.OnDeath, new[] { effect }, usageLimit: limit);
        var binding = Binding("binding.once", SourceId, abilityId, TriggerIds.OnDeath);
        var queue = new DeterministicTriggerQueue();
        queue.Enqueue(new TriggerQueueItem(new EntityDiedDomainEvent(1, SourceId), 0, binding));
        queue.Enqueue(new TriggerQueueItem(new EntityDiedDomainEvent(2, SourceId), 0, binding));

        var result = Engine(new[] { ability }, new[] { binding }).Resolve(CreateState(), queue);

        Assert.Equal(6m, result.State.GetRequiredTarget(OwnerId).GetRequiredResource(ResourceId));
        Assert.Equal(2, result.ProcessedTriggers);
        Assert.Equal(1, Assert.Single(result.State.AbilityUsage).TotalResolutions);
    }

    [Fact]
    public void Resolve_FailedConditionDoesNotConsumeUsage()
    {
        var abilityId = StableId.Parse("ability.conditioned");
        var condition = new PredicateConditionNode(new ConditionPredicateSpec(
            ConditionIds.TurnCompare,
            Bag(
                Pair("operator", new EnumParameterValue("GT")),
                Pair("value", FormulaExpression.Constant(99m)))));
        var effect = new EffectDefinition(EffectIds.Heal, Bag(
            Pair("target", Selector(TargetScope.Source)),
            Pair("amount", FormulaExpression.Constant(2m))));
        var ability = Ability(
            abilityId,
            TriggerIds.OnDeath,
            new[] { effect },
            condition,
            new UsageLimitSpec(UsageLimitIds.OncePerMatch));
        var binding = Binding("binding.conditioned", SourceId, abilityId, TriggerIds.OnDeath);
        var queue = new DeterministicTriggerQueue();
        queue.Enqueue(new TriggerQueueItem(new EntityDiedDomainEvent(1, SourceId), 0, binding));

        var result = Engine(new[] { ability }, new[] { binding }).Resolve(CreateState(), queue);

        Assert.Empty(result.Events);
        Assert.Empty(result.State.AbilityUsage);
    }

    [Fact]
    public void Resolve_PlayerChoiceRollsBackSpeculativePrefixAndDoesNotConsumeUsage()
    {
        var abilityId = StableId.Parse("ability.choice_sequence");
        var resource = new EffectDefinition(EffectIds.ChangeResource, Bag(
            Pair("target", Selector(TargetScope.Owner)),
            Pair("resourceId", new StableIdParameterValue(ResourceId)),
            Pair("amount", FormulaExpression.Constant(2m))));
        var healChoice = new EffectDefinition(EffectIds.Heal, Bag(
            Pair("target", Selector(TargetScope.Unit, TargetRelation.Friendly, TargetZone.Board, TargetSelection.PlayerChoice)),
            Pair("amount", FormulaExpression.Constant(2m))));
        var ability = Ability(abilityId, TriggerIds.OnDeath, new[] { resource, healChoice });
        var binding = Binding("binding.choice", SourceId, abilityId, TriggerIds.OnDeath);
        var queue = new DeterministicTriggerQueue();
        queue.Enqueue(new TriggerQueueItem(new EntityDiedDomainEvent(1, SourceId), 0, binding));
        var original = CreateState();

        var result = Engine(new[] { ability }, new[] { binding }).Resolve(original, queue);

        Assert.True(result.RequiresPlayerChoice);
        Assert.Equal(5m, result.State.GetRequiredTarget(OwnerId).GetRequiredResource(ResourceId));
        Assert.Empty(result.Events);
        Assert.Empty(result.State.AbilityUsage);
        Assert.Equal(new[] { 1 }, result.PendingChoice!.EffectChoice.EffectPath);
    }

    [Fact]
    public void UsageRules_CooldownWaitsConfiguredWholeTurns()
    {
        var limit = new UsageLimitSpec(UsageLimitIds.CooldownTurns, Bag(
            Pair("turns", new IntegerParameterValue(1))));
        var record = new AbilityUsageRecord(SourceId, StableId.Parse("ability.cooldown"), 1, 3, 1, 3);

        Assert.False(AbilityUsageRules.CanResolve(limit, record, 4));
        Assert.True(AbilityUsageRules.CanResolve(limit, record, 5));
    }

    [Fact]
    public void UsageRules_MaxWhileInZoneIsRejectedUntilZoneResidencyIsAuthoritative()
    {
        var limit = new UsageLimitSpec(UsageLimitIds.MaxWhileInZone, Bag(
            Pair("count", new IntegerParameterValue(1))));

        Assert.Throws<NotSupportedException>(() => AbilityUsageRules.CanResolve(limit, null, 3));
    }

    [Fact]
    public void Resolve_StopsCyclicTriggerChainsAtConfiguredBudget()
    {
        var abilityId = StableId.Parse("ability.damage_loop");
        var effect = new EffectDefinition(EffectIds.Damage, Bag(
            Pair("target", Selector(TargetScope.Source)),
            Pair("amount", FormulaExpression.Constant(1m)),
            Pair("damageType", new EnumParameterValue("TRUE"))));
        var ability = Ability(abilityId, TriggerIds.OnDamaged, new[] { effect });
        var binding = Binding("binding.loop", TargetId, abilityId, TriggerIds.OnDamaged);
        var initial = new StateTransitionEngine().Apply(CreateState(targetHealth: 50m), DamageOperation(1m));
        var queue = QueueFromEvents(new[] { binding }, initial.Events);
        var engine = Engine(new[] { ability }, new[] { binding }, triggerBudget: 2);

        Assert.Throws<ResolutionBudgetExceededException>(() => engine.Resolve(initial.State, queue));
    }

    private static AbilityResolutionEngine Engine(
        IEnumerable<AbilityDefinition> abilities,
        IEnumerable<TriggerBinding> bindings,
        int triggerBudget = 256) =>
        new(new AbilityRuntimeCatalog(abilities), new TriggerDiscovery(bindings), triggerStepBudget: triggerBudget);

    private static AbilityDefinition Ability(
        StableId abilityId,
        StableId triggerId,
        IEnumerable<EffectDefinition> effects,
        ConditionNode? condition = null,
        UsageLimitSpec? usageLimit = null) =>
        new(
            new GameplayDefinitionHeader(abilityId, 1, 1, ContentStatus.Published, "loc." + abilityId.Value.Replace('.', '_')),
            new TriggerSpec(triggerId),
            effects,
            condition,
            usageLimit);

    private static TriggerBinding Binding(string id, StableId sourceId, StableId abilityId, StableId triggerId) =>
        new(StableId.Parse(id), sourceId, abilityId, triggerId);

    private static DeterministicTriggerQueue QueueFromEvents(
        IEnumerable<TriggerBinding> bindings,
        IEnumerable<DomainEvent> events)
    {
        var queue = new DeterministicTriggerQueue();
        new TriggerDiscovery(bindings).DiscoverInto(events, queue);
        return queue;
    }

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

    private static ResolvedEffectOperation DamageOperation(decimal amount) =>
        new(ResolvedEffectOperationKind.DamageRequest, EffectIds.Damage, SourceId, TargetId, amount, qualifier: "TRUE");

    private static MatchStateSnapshot CreateState(decimal targetHealth = 20m)
    {
        var targets = new[]
        {
            new RuntimeTarget(OwnerId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 5m) }),
            new RuntimeTarget(OpponentId, RuntimeTargetKind.Player, resources: new[] { new KeyValuePair<StableId, decimal>(ResourceId, 5m) }),
            new RuntimeTarget(StableId.Parse("lane.left"), RuntimeTargetKind.Lane, laneIndex: 0),
            Unit(SourceId, OwnerId, 3m, 20m, 0m, 0, 20m),
            Unit(TargetId, OpponentId, 2m, 20m, 0m, 0, targetHealth)
        };
        return new MatchStateSnapshot(targets, 3, StableId.Parse("phase.main_action"), 1);
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
