/**
 * ABILITY_RESOLUTION_ENGINE
 * Purpose: Drains deterministic trigger work into validated ability conditions, usage limits, sequential effects, state transitions, and newly discovered reactions.
 * Connections: Coordinates AbilityRuntimeCatalog, GameplayRuntimeEvaluator, EffectSequenceEngine, MatchStateSnapshot, TriggerDiscovery, and DeterministicTriggerQueue.
 * Risk: High because this loop is the authoritative chain-resolution boundary and must prevent recursion, partial player-choice commits, and infinite trigger cycles.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Abilities;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Modifiers;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Triggers;

namespace HyuHeroes.Gameplay.Simulation;

public sealed class PendingAbilityChoice
{
    public PendingAbilityChoice(
        TriggerQueueItem queueItem,
        AbilityDefinition ability,
        PendingEffectSequenceChoice effectChoice)
    {
        QueueItem = queueItem ?? throw new ArgumentNullException(nameof(queueItem));
        Ability = ability ?? throw new ArgumentNullException(nameof(ability));
        EffectChoice = effectChoice ?? throw new ArgumentNullException(nameof(effectChoice));
    }

    public TriggerQueueItem QueueItem { get; }
    public AbilityDefinition Ability { get; }
    public PendingEffectSequenceChoice EffectChoice { get; }
}

public sealed class AbilityResolutionResult
{
    public AbilityResolutionResult(
        MatchStateSnapshot state,
        IEnumerable<DomainEvent> events,
        int processedTriggers,
        PendingAbilityChoice? pendingChoice = null)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Events = new ReadOnlyCollection<DomainEvent>((events ?? throw new ArgumentNullException(nameof(events))).ToArray());
        ProcessedTriggers = processedTriggers;
        PendingChoice = pendingChoice;
    }

    public MatchStateSnapshot State { get; }
    public IReadOnlyList<DomainEvent> Events { get; }
    public int ProcessedTriggers { get; }
    public PendingAbilityChoice? PendingChoice { get; }
    public bool RequiresPlayerChoice => PendingChoice is not null;
}

public sealed class ResolutionBudgetExceededException : InvalidOperationException
{
    public ResolutionBudgetExceededException(int budget)
        : base($"Ability trigger resolution exceeded the configured step budget of {budget}.")
    {
        Budget = budget;
    }

    public int Budget { get; }
}

public sealed class AbilityResolutionEngine
{
    private const int DefaultTriggerStepBudget = 256;
    private readonly AbilityRuntimeCatalog _catalog;
    private readonly EffectSequenceEngine _effects;
    private readonly int _triggerStepBudget;
    private readonly TriggerDiscovery _triggerDiscovery;

    public AbilityResolutionEngine(
        AbilityRuntimeCatalog catalog,
        TriggerDiscovery triggerDiscovery,
        EffectSequenceEngine? effects = null,
        int triggerStepBudget = DefaultTriggerStepBudget)
    {
        if (triggerStepBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(triggerStepBudget), "Trigger step budget must be positive.");
        }

        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _triggerDiscovery = triggerDiscovery ?? throw new ArgumentNullException(nameof(triggerDiscovery));
        _effects = effects ?? new EffectSequenceEngine();
        _triggerStepBudget = triggerStepBudget;
    }

    public AbilityResolutionResult Resolve(MatchStateSnapshot state, DeterministicTriggerQueue queue)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (queue is null) throw new ArgumentNullException(nameof(queue));

        var currentState = state;
        var emittedEvents = new List<DomainEvent>();
        var processedTriggers = 0;
        while (queue.Count > 0)
        {
            if (processedTriggers >= _triggerStepBudget)
            {
                throw new ResolutionBudgetExceededException(_triggerStepBudget);
            }

            var item = queue.Dequeue();
            processedTriggers++;
            var ability = _catalog.GetRequired(item.Binding.AbilityId);
            ValidateBinding(item, ability);
            if (!CanResolveAbility(currentState, item, ability))
            {
                continue;
            }

            var activeTargetId = item.OriginatingEvent.TargetId ?? item.Binding.SourceId;
            var speculative = _effects.Execute(currentState, item.Binding.SourceId, ability.Effects, activeTargetId);
            if (speculative.PendingChoice is not null)
            {
                var pending = new PendingAbilityChoice(item, ability, speculative.PendingChoice);
                return new AbilityResolutionResult(currentState, emittedEvents, processedTriggers, pending);
            }

            currentState = speculative.State.With(
                abilityUsage: AbilityUsageRules.RecordResolution(
                    speculative.State.AbilityUsage,
                    item.Binding.SourceId,
                    ability.Header.Id,
                    speculative.State.TurnNumber));
            emittedEvents.AddRange(speculative.Events);
            _triggerDiscovery.DiscoverInto(speculative.Events, queue);
        }

        return new AbilityResolutionResult(currentState, emittedEvents, processedTriggers);
    }

    private static void ValidateBinding(TriggerQueueItem item, AbilityDefinition ability)
    {
        if (ability.Header.Id != item.Binding.AbilityId)
        {
            throw new InvalidOperationException("Trigger binding ability ID does not match the runtime ability definition.");
        }

        if (ability.Trigger.TypeId != item.Binding.TriggerId)
        {
            throw new InvalidOperationException(
                $"Ability '{ability.Header.Id}' is bound to '{item.Binding.TriggerId}' but declares trigger '{ability.Trigger.TypeId}'.");
        }
    }

    private static bool CanResolveAbility(
        MatchStateSnapshot state,
        TriggerQueueItem item,
        AbilityDefinition ability)
    {
        var evaluator = new GameplayRuntimeEvaluator(statPipeline: new StatModifierPipeline(state.StatModifiers));
        var runtime = CreateRuntimeContext(state, item);
        if (ability.Condition is not null && !evaluator.EvaluateCondition(ability.Condition, runtime))
        {
            return false;
        }

        var usage = AbilityUsageRules.Find(state.AbilityUsage, item.Binding.SourceId, ability.Header.Id);
        return AbilityUsageRules.CanResolve(ability.UsageLimit, usage, state.TurnNumber);
    }

    private static GameplayRuntimeContext CreateRuntimeContext(MatchStateSnapshot state, TriggerQueueItem item)
    {
        var source = state.GetRequiredTarget(item.Binding.SourceId);
        var ownerId = source.EffectiveOwnerId
            ?? throw new InvalidOperationException($"Ability source '{source.RuntimeId}' has no effective owner.");
        var opponentId = state.Targets
            .Where(target => target.Kind == RuntimeTargetKind.Player && target.RuntimeId != ownerId)
            .Select(target => target.RuntimeId)
            .Single();
        return new GameplayRuntimeContext(
            state.Targets,
            source.RuntimeId,
            ownerId,
            opponentId,
            state.TurnNumber,
            state.PhaseId,
            state.LaneCount,
            source.LaneIndex,
            item.OriginatingEvent.TargetId ?? source.RuntimeId);
    }
}
