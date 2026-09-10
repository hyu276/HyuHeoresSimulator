/**
 * DEFAULT_CONDITION_PREDICATE_RESOLVER
 * Purpose: Executes the built-in schema-v1 condition predicates against immutable runtime targets without embedding card-specific logic.
 * Connections: Invoked by ConditionEvaluator and delegates selectors/formulas to GameplayRuntimeEvaluator for consistent recursive evaluation.
 * Risk: High because predicate semantics decide whether authoritative abilities, filters, and future effects are eligible to resolve.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Conditions;

public sealed class DefaultConditionPredicateResolver : IConditionPredicateResolver
{
    private static readonly StableId MaxHealthStatId = StableId.Parse("stat.max_health");
    private static readonly IReadOnlyDictionary<string, Func<decimal, decimal, bool>> Comparers =
        new Dictionary<string, Func<decimal, decimal, bool>>(StringComparer.Ordinal)
        {
            ["EQ"] = (left, right) => left == right,
            ["NE"] = (left, right) => left != right,
            ["LT"] = (left, right) => left < right,
            ["LTE"] = (left, right) => left <= right,
            ["GT"] = (left, right) => left > right,
            ["GTE"] = (left, right) => left >= right
        };
    private static readonly IReadOnlyDictionary<string, CardType> CardTypes =
        new Dictionary<string, CardType>(StringComparer.Ordinal)
        {
            ["UNIT"] = CardType.Unit,
            ["ACTION"] = CardType.Action,
            ["ENVIRONMENT"] = CardType.Environment,
            ["HERO_ABILITY"] = CardType.HeroAbility
        };
    private static readonly IReadOnlyDictionary<string, TargetZone> Zones =
        new Dictionary<string, TargetZone>(StringComparer.Ordinal)
        {
            ["BOARD"] = TargetZone.Board,
            ["HAND"] = TargetZone.Hand,
            ["DECK"] = TargetZone.Deck,
            ["GRAVEYARD"] = TargetZone.Graveyard
        };

    private readonly GameplayRuntimeEvaluator _evaluator;
    private readonly GameplayRuntimeContext _runtime;
    private readonly IReadOnlyDictionary<StableId, Func<ParameterBag, bool>> _handlers;

    public DefaultConditionPredicateResolver(GameplayRuntimeEvaluator evaluator, GameplayRuntimeContext runtime)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _handlers = new Dictionary<StableId, Func<ParameterBag, bool>>
        {
            [ConditionIds.HasTag] = EvaluateHasTag,
            [ConditionIds.HasKeyword] = EvaluateHasKeyword,
            [ConditionIds.StatCompare] = EvaluateStatCompare,
            [ConditionIds.ResourceCompare] = EvaluateResourceCompare,
            [ConditionIds.LaneIsEmpty] = EvaluateLaneIsEmpty,
            [ConditionIds.CardTypeIs] = EvaluateCardTypeIs,
            [ConditionIds.OwnerIs] = EvaluateOwnerIs,
            [ConditionIds.TargetIsDamaged] = EvaluateTargetIsDamaged,
            [ConditionIds.CountMatchingCompare] = EvaluateCountMatchingCompare,
            [ConditionIds.TurnCompare] = EvaluateTurnCompare,
            [ConditionIds.PhaseIs] = EvaluatePhaseIs,
            [ConditionIds.ZoneIs] = EvaluateZoneIs
        };
    }

    public bool Evaluate(ConditionPredicateSpec predicate)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        if (!_handlers.TryGetValue(predicate.TypeId, out var handler))
        {
            throw new KeyNotFoundException($"No schema-v1 runtime handler is registered for condition '{predicate.TypeId}'.");
        }

        return handler(predicate.Parameters);
    }

    private bool EvaluateHasTag(ParameterBag parameters)
    {
        var tagId = parameters.GetRequired<StableIdParameterValue>("tagId").Value;
        return ResolveSubjects(parameters).Any(subject => subject.Tags.Contains(tagId));
    }

    private bool EvaluateHasKeyword(ParameterBag parameters)
    {
        var keywordId = parameters.GetRequired<StableIdParameterValue>("keywordId").Value;
        return ResolveSubjects(parameters).Any(subject => subject.Keywords.Contains(keywordId));
    }

    private bool EvaluateStatCompare(ParameterBag parameters)
    {
        var statId = parameters.GetRequired<StableIdParameterValue>("statId").Value;
        return ResolveSubjects(parameters).Any(subject =>
            subject.TryGetStat(statId, out var value) && Compare(value, EvaluateComparisonFormula(parameters, subject), parameters));
    }

    private bool EvaluateResourceCompare(ParameterBag parameters)
    {
        var resourceId = parameters.GetRequired<StableIdParameterValue>("resourceId").Value;
        return ResolveSubjects(parameters).Any(subject =>
            subject.TryGetResource(resourceId, out var value) && Compare(value, EvaluateComparisonFormula(parameters, subject), parameters));
    }

    private bool EvaluateLaneIsEmpty(ParameterBag parameters)
    {
        var lanes = ResolveSubjects(parameters).Where(subject => subject.Kind == RuntimeTargetKind.Lane).ToArray();
        return lanes.Length > 0 && lanes.All(IsLaneEmpty);
    }

    private bool EvaluateCardTypeIs(ParameterBag parameters)
    {
        var rawCardType = parameters.GetRequired<EnumParameterValue>("cardType").Value;
        var cardType = GetRequired(CardTypes, rawCardType, "card type");
        return ResolveSubjects(parameters).Any(subject => subject.CardType == cardType);
    }

    private bool EvaluateOwnerIs(ParameterBag parameters)
    {
        var relation = parameters.GetRequired<EnumParameterValue>("relation").Value;
        return relation switch
        {
            "FRIENDLY" => ResolveSubjects(parameters).Any(subject => subject.EffectiveOwnerId == _runtime.OwnerId),
            "ENEMY" => ResolveSubjects(parameters).Any(subject => subject.EffectiveOwnerId == _runtime.OpponentId),
            _ => throw new InvalidOperationException($"Unsupported owner relation '{relation}'.")
        };
    }

    private bool EvaluateTargetIsDamaged(ParameterBag parameters) =>
        ResolveSubjects(parameters).Any(IsDamaged);

    private bool EvaluateCountMatchingCompare(ParameterBag parameters)
    {
        var count = ResolveSubjects(parameters).Count;
        var expected = EvaluateFormula(parameters.GetRequired<FormulaParameterValue>("value").Value, _runtime);
        return Compare(count, expected, parameters);
    }

    private bool EvaluateTurnCompare(ParameterBag parameters)
    {
        var expected = EvaluateFormula(parameters.GetRequired<FormulaParameterValue>("value").Value, _runtime);
        return Compare(_runtime.TurnNumber, expected, parameters);
    }

    private bool EvaluatePhaseIs(ParameterBag parameters) =>
        _runtime.PhaseId == parameters.GetRequired<StableIdParameterValue>("phaseId").Value;

    private bool EvaluateZoneIs(ParameterBag parameters)
    {
        var rawZone = parameters.GetRequired<EnumParameterValue>("zone").Value;
        var zone = GetRequired(Zones, rawZone, "zone");
        return ResolveSubjects(parameters).Any(subject => subject.Zone == zone);
    }

    private IReadOnlyList<RuntimeTarget> ResolveSubjects(ParameterBag parameters) =>
        _evaluator.ResolveTargetsForEvaluation(
            parameters.GetRequired<SelectorParameterValue>("subject").Value,
            _runtime);

    private decimal EvaluateComparisonFormula(ParameterBag parameters, RuntimeTarget subject) =>
        EvaluateFormula(parameters.GetRequired<FormulaParameterValue>("value").Value, _runtime.WithActiveTarget(subject.RuntimeId));

    private decimal EvaluateFormula(FormulaExpression formula, GameplayRuntimeContext context) =>
        _evaluator.EvaluateFormula(formula, context);

    private bool IsLaneEmpty(RuntimeTarget lane)
    {
        return !_runtime.Targets.Any(target =>
            target.LaneIndex == lane.LaneIndex &&
            target.Zone == TargetZone.Board &&
            (target.Kind == RuntimeTargetKind.Unit || target.Kind == RuntimeTargetKind.Hero));
    }

    private static bool IsDamaged(RuntimeTarget subject)
    {
        if (!subject.TryGetCurrentHealth(MaxHealthStatId, out var currentHealth) ||
            !subject.TryGetStat(MaxHealthStatId, out var maximumHealth))
        {
            return false;
        }

        return currentHealth < maximumHealth;
    }

    private static bool Compare(decimal left, decimal right, ParameterBag parameters)
    {
        var comparison = parameters.GetRequired<EnumParameterValue>("operator").Value;
        return GetRequired(Comparers, comparison, "comparison operator")(left, right);
    }

    private static TValue GetRequired<TValue>(
        IReadOnlyDictionary<string, TValue> values,
        string key,
        string kind)
    {
        if (values.TryGetValue(key, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Unsupported {kind} '{key}'.");
    }
}
