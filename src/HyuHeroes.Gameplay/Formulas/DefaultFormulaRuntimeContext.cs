/**
 * DEFAULT_FORMULA_RUNTIME_CONTEXT
 * Purpose: Resolves schema-v1 formula variables and COUNT selectors from immutable runtime state using effective stats.
 * Connections: Used by GameplayRuntimeEvaluator through FormulaEvaluationContext and delegates stats and target counting to shared runtime services.
 * Risk: High because numeric runtime values directly influence costs, damage, healing, scaling, and deterministic replay outcomes.
 */
using System;
using System.Collections.Generic;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Formulas;

public sealed class DefaultFormulaRuntimeContext : IFormulaVariableResolver, ITargetCountResolver
{
    private static readonly StableId AttackStatId = StableId.Parse("stat.attack");
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly StableId PrimaryResourceId = StableId.Parse("resource.primary");
    private static readonly StableId SourceAttackId = StableId.Parse("variable.source.attack");
    private static readonly StableId SourceHealthId = StableId.Parse("variable.source.health");
    private static readonly StableId SourceMaxHealthId = StableId.Parse("variable.source.max_health");
    private static readonly StableId TargetAttackId = StableId.Parse("variable.target.attack");
    private static readonly StableId TargetHealthId = StableId.Parse("variable.target.health");
    private static readonly StableId OwnerResourceId = StableId.Parse("variable.owner.resource");
    private static readonly StableId OpponentResourceId = StableId.Parse("variable.opponent.resource");
    private static readonly StableId TurnNumberId = StableId.Parse("variable.turn.number");

    private readonly GameplayRuntimeEvaluator _evaluator;
    private readonly GameplayRuntimeContext _runtime;
    private readonly IReadOnlyDictionary<StableId, Func<decimal>> _variableResolvers;

    public DefaultFormulaRuntimeContext(GameplayRuntimeEvaluator evaluator, GameplayRuntimeContext runtime)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _variableResolvers = new Dictionary<StableId, Func<decimal>>
        {
            [SourceAttackId] = () => _evaluator.ResolveStat(_runtime.Source, AttackStatId),
            [SourceHealthId] = () => GetRequiredHealth(_runtime.Source),
            [SourceMaxHealthId] = () => _evaluator.ResolveStat(_runtime.Source, MaxHealthStatId),
            [TargetAttackId] = () => _evaluator.ResolveStat(_runtime.ActiveTarget, AttackStatId),
            [TargetHealthId] = () => GetRequiredHealth(_runtime.ActiveTarget),
            [OwnerResourceId] = () => _runtime.Owner.GetRequiredResource(PrimaryResourceId),
            [OpponentResourceId] = () => _runtime.Opponent.GetRequiredResource(PrimaryResourceId),
            [TurnNumberId] = () => _runtime.TurnNumber
        };
    }

    public decimal Resolve(StableId variableId)
    {
        if (!_variableResolvers.TryGetValue(variableId, out var resolver))
        {
            throw new KeyNotFoundException($"No schema-v1 runtime resolver is registered for formula variable '{variableId}'.");
        }

        return resolver();
    }

    public int Count(TargetSelectorSpec selector)
    {
        if (selector.Selection != TargetSelection.All)
        {
            throw new InvalidOperationException("Formula COUNT requires ALL selection and cannot consume RNG or player choice.");
        }

        return _evaluator.ResolveTargetsForEvaluation(selector, _runtime).Count;
    }

    private decimal GetRequiredHealth(RuntimeTarget target)
    {
        if (target.CurrentHealth is { } currentHealth)
        {
            return currentHealth;
        }

        if (target.TryGetStat(MaxHealthStatId, out _))
        {
            return _evaluator.ResolveStat(target, MaxHealthStatId);
        }

        throw new KeyNotFoundException($"Runtime target '{target.RuntimeId}' does not expose health.");
    }
}
