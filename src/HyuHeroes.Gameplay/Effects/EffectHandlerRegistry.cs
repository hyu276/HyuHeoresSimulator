/**
 * EFFECT_HANDLER_REGISTRY
 * Purpose: Converts validated declarative effects into deterministic resolved operations without mutating authoritative match state.
 * Connections: Uses GameplayRuntimeEvaluator for selectors, conditions, formulas, and feeds future reducers, damage resolution, and event queues.
 * Risk: High because effect planning defines the executable boundary between authoring data and authoritative state-transition logic.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Runtime;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Effects;

public enum ResolvedEffectOperationKind
{
    DamageRequest,
    Heal,
    Draw,
    AddStatModifier,
    SetStat,
    ChangeResource
}

public sealed class ResolvedEffectOperation
{
    public ResolvedEffectOperation(
        ResolvedEffectOperationKind kind,
        StableId effectId,
        StableId sourceId,
        StableId targetId,
        decimal amount,
        StableId? primaryReferenceId = null,
        StableId? secondaryReferenceId = null,
        string? qualifier = null)
    {
        if (!Enum.IsDefined(typeof(ResolvedEffectOperationKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Resolved effect operation kind is not defined.");
        }

        if (effectId == default || sourceId == default || targetId == default)
        {
            throw new ArgumentException("Resolved effect IDs must be non-default StableIds.");
        }

        ValidateOptionalId(primaryReferenceId, nameof(primaryReferenceId));
        ValidateOptionalId(secondaryReferenceId, nameof(secondaryReferenceId));
        Kind = kind;
        EffectId = effectId;
        SourceId = sourceId;
        TargetId = targetId;
        Amount = amount;
        PrimaryReferenceId = primaryReferenceId;
        SecondaryReferenceId = secondaryReferenceId;
        Qualifier = qualifier;
    }

    public ResolvedEffectOperationKind Kind { get; }
    public StableId EffectId { get; }
    public StableId SourceId { get; }
    public StableId TargetId { get; }
    public decimal Amount { get; }
    public StableId? PrimaryReferenceId { get; }
    public StableId? SecondaryReferenceId { get; }
    public string? Qualifier { get; }

    private static void ValidateOptionalId(StableId? value, string parameterName)
    {
        if (value is { } concreteId && concreteId == default)
        {
            throw new ArgumentException("Optional resolved-effect IDs must be null or non-default StableIds.", parameterName);
        }
    }
}

public sealed class PendingEffectChoice
{
    public PendingEffectChoice(StableId effectId, string parameterName, TargetResolution resolution)
    {
        EffectId = effectId == default
            ? throw new ArgumentException("Pending effect ID must be a non-default StableId.", nameof(effectId))
            : effectId;
        ParameterName = string.IsNullOrWhiteSpace(parameterName)
            ? throw new ArgumentException("Pending choice parameter name cannot be empty.", nameof(parameterName))
            : parameterName;
        Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
    }

    public StableId EffectId { get; }
    public string ParameterName { get; }
    public TargetResolution Resolution { get; }
}

public sealed class EffectExecutionResult
{
    public EffectExecutionResult(IEnumerable<ResolvedEffectOperation>? operations = null, PendingEffectChoice? pendingChoice = null)
    {
        Operations = new ReadOnlyCollection<ResolvedEffectOperation>((operations ?? Array.Empty<ResolvedEffectOperation>()).ToArray());
        PendingChoice = pendingChoice;
    }

    public IReadOnlyList<ResolvedEffectOperation> Operations { get; }
    public PendingEffectChoice? PendingChoice { get; }
    public bool RequiresPlayerChoice => PendingChoice is not null;
}

public sealed class EffectExecutionContext
{
    public EffectExecutionContext(GameplayEffectExecutor executor, GameplayRuntimeEvaluator evaluator, GameplayRuntimeContext runtime)
    {
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        Evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public GameplayEffectExecutor Executor { get; }
    public GameplayRuntimeEvaluator Evaluator { get; }
    public GameplayRuntimeContext Runtime { get; }
}

public sealed class EffectHandlerRegistration
{
    private readonly Func<EffectDefinition, EffectExecutionContext, EffectExecutionResult> _handler;

    public EffectHandlerRegistration(StableId effectId, Func<EffectDefinition, EffectExecutionContext, EffectExecutionResult> handler)
    {
        EffectId = effectId == default
            ? throw new ArgumentException("Effect handler ID must be a non-default StableId.", nameof(effectId))
            : effectId;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public StableId EffectId { get; }

    public EffectExecutionResult Resolve(EffectDefinition effect, EffectExecutionContext context) => _handler(effect, context);
}

public sealed class EffectHandlerRegistry
{
    private readonly IReadOnlyDictionary<StableId, EffectHandlerRegistration> _registrations;

    public EffectHandlerRegistry(IEnumerable<EffectHandlerRegistration> registrations)
    {
        if (registrations is null)
        {
            throw new ArgumentNullException(nameof(registrations));
        }

        var registrationArray = registrations.ToArray();
        if (registrationArray.Any(registration => registration is null))
        {
            throw new ArgumentException("Effect handler registry cannot contain null registrations.", nameof(registrations));
        }

        if (registrationArray.GroupBy(registration => registration.EffectId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Runtime effect handler IDs must be unique.", nameof(registrations));
        }

        _registrations = new ReadOnlyDictionary<StableId, EffectHandlerRegistration>(
            registrationArray.ToDictionary(registration => registration.EffectId));
    }

    public EffectHandlerRegistration GetRequired(StableId effectId) =>
        _registrations.TryGetValue(effectId, out var registration)
            ? registration
            : throw new KeyNotFoundException($"No runtime effect handler is registered for '{effectId}'.");
}

public sealed class GameplayEffectExecutor
{
    private readonly EffectHandlerRegistry _registry;
    private readonly GameplayRuntimeEvaluator _evaluator;

    public GameplayEffectExecutor(GameplayRuntimeEvaluator evaluator, EffectHandlerRegistry? registry = null)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _registry = registry ?? DefaultEffectHandlerRegistry.Create();
    }

    public EffectExecutionResult Resolve(EffectDefinition effect, GameplayRuntimeContext runtime)
    {
        if (effect is null)
        {
            throw new ArgumentNullException(nameof(effect));
        }

        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        var context = new EffectExecutionContext(this, _evaluator, runtime);
        return _registry.GetRequired(effect.TypeId).Resolve(effect, context);
    }

    public EffectExecutionResult ResolveAll(IEnumerable<EffectDefinition> effects, GameplayRuntimeContext runtime)
    {
        if (effects is null)
        {
            throw new ArgumentNullException(nameof(effects));
        }

        var operations = new List<ResolvedEffectOperation>();
        foreach (var effect in effects)
        {
            var result = Resolve(effect, runtime);
            operations.AddRange(result.Operations);
            if (result.PendingChoice is not null)
            {
                return new EffectExecutionResult(operations, result.PendingChoice);
            }
        }

        return new EffectExecutionResult(operations);
    }
}

public static class DefaultEffectHandlerRegistry
{
    public static EffectHandlerRegistry Create() =>
        new(new[]
        {
            Registration(EffectIds.Damage, (effect, context) => ResolveNumericTarget(effect, context, ResolvedEffectOperationKind.DamageRequest, "damageType")),
            Registration(EffectIds.Heal, (effect, context) => ResolveNumericTarget(effect, context, ResolvedEffectOperationKind.Heal)),
            Registration(EffectIds.Draw, (effect, context) => ResolveNumericTarget(effect, context, ResolvedEffectOperationKind.Draw)),
            Registration(EffectIds.ModifyStat, ResolveModifyStat),
            Registration(EffectIds.SetStat, ResolveSetStat),
            Registration(EffectIds.ChangeResource, ResolveChangeResource),
            Registration(EffectIds.Conditional, ResolveConditional)
        });

    private static EffectHandlerRegistration Registration(
        StableId effectId,
        Func<EffectDefinition, EffectExecutionContext, EffectExecutionResult> handler) =>
        new(effectId, handler);

    private static EffectExecutionResult ResolveNumericTarget(
        EffectDefinition effect,
        EffectExecutionContext context,
        ResolvedEffectOperationKind kind,
        string? qualifierParameter = null)
    {
        var resolution = ResolveTarget(effect, context, "target");
        if (resolution.RequiresPlayerChoice)
        {
            return Pending(effect, "target", resolution);
        }

        var amountFormula = effect.Parameters.GetRequired<FormulaParameterValue>("amount").Value;
        var qualifier = qualifierParameter is null
            ? null
            : effect.Parameters.GetRequired<EnumParameterValue>(qualifierParameter).Value;
        var operations = resolution.SelectedTargets.Select(target =>
            new ResolvedEffectOperation(
                kind,
                effect.TypeId,
                context.Runtime.SourceId,
                target.RuntimeId,
                context.Evaluator.EvaluateFormula(amountFormula, context.Runtime.WithActiveTarget(target.RuntimeId)),
                qualifier: qualifier));
        return new EffectExecutionResult(operations);
    }

    private static EffectExecutionResult ResolveModifyStat(EffectDefinition effect, EffectExecutionContext context)
    {
        var resolution = ResolveTarget(effect, context, "target");
        if (resolution.RequiresPlayerChoice)
        {
            return Pending(effect, "target", resolution);
        }

        var statId = effect.Parameters.GetRequired<StableIdParameterValue>("statId").Value;
        var durationId = effect.Parameters.GetRequired<StableIdParameterValue>("durationId").Value;
        var operation = effect.Parameters.GetRequired<EnumParameterValue>("operation").Value;
        var formula = effect.Parameters.GetRequired<FormulaParameterValue>("value").Value;
        var operations = resolution.SelectedTargets.Select(target =>
            new ResolvedEffectOperation(
                ResolvedEffectOperationKind.AddStatModifier,
                effect.TypeId,
                context.Runtime.SourceId,
                target.RuntimeId,
                context.Evaluator.EvaluateFormula(formula, context.Runtime.WithActiveTarget(target.RuntimeId)),
                statId,
                durationId,
                operation));
        return new EffectExecutionResult(operations);
    }

    private static EffectExecutionResult ResolveSetStat(EffectDefinition effect, EffectExecutionContext context)
    {
        var resolution = ResolveTarget(effect, context, "target");
        if (resolution.RequiresPlayerChoice)
        {
            return Pending(effect, "target", resolution);
        }

        var statId = effect.Parameters.GetRequired<StableIdParameterValue>("statId").Value;
        var formula = effect.Parameters.GetRequired<FormulaParameterValue>("value").Value;
        var operations = resolution.SelectedTargets.Select(target =>
            new ResolvedEffectOperation(
                ResolvedEffectOperationKind.SetStat,
                effect.TypeId,
                context.Runtime.SourceId,
                target.RuntimeId,
                context.Evaluator.EvaluateFormula(formula, context.Runtime.WithActiveTarget(target.RuntimeId)),
                statId));
        return new EffectExecutionResult(operations);
    }

    private static EffectExecutionResult ResolveChangeResource(EffectDefinition effect, EffectExecutionContext context)
    {
        var resolution = ResolveTarget(effect, context, "target");
        if (resolution.RequiresPlayerChoice)
        {
            return Pending(effect, "target", resolution);
        }

        var resourceId = effect.Parameters.GetRequired<StableIdParameterValue>("resourceId").Value;
        var formula = effect.Parameters.GetRequired<FormulaParameterValue>("amount").Value;
        var operations = resolution.SelectedTargets.Select(target =>
            new ResolvedEffectOperation(
                ResolvedEffectOperationKind.ChangeResource,
                effect.TypeId,
                context.Runtime.SourceId,
                target.RuntimeId,
                context.Evaluator.EvaluateFormula(formula, context.Runtime.WithActiveTarget(target.RuntimeId)),
                resourceId));
        return new EffectExecutionResult(operations);
    }

    private static EffectExecutionResult ResolveConditional(EffectDefinition effect, EffectExecutionContext context)
    {
        var condition = effect.Parameters.GetRequired<ConditionParameterValue>("if").Value;
        var branchName = context.Evaluator.EvaluateCondition(condition, context.Runtime) ? "thenEffects" : "elseEffects";
        if (!effect.Parameters.TryGet(branchName, out var branchValue) || branchValue is not EffectListParameterValue branch)
        {
            return new EffectExecutionResult();
        }

        return context.Executor.ResolveAll(branch.Value, context.Runtime);
    }

    private static TargetResolution ResolveTarget(EffectDefinition effect, EffectExecutionContext context, string parameterName) =>
        context.Evaluator.ResolveTargets(
            effect.Parameters.GetRequired<SelectorParameterValue>(parameterName).Value,
            context.Runtime);

    private static EffectExecutionResult Pending(EffectDefinition effect, string parameterName, TargetResolution resolution) =>
        new(pendingChoice: new PendingEffectChoice(effect.TypeId, parameterName, resolution));
}
