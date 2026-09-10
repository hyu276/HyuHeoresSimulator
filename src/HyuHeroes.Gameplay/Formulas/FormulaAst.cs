/**
 * FORMULA_AST
 * Purpose: Defines and evaluates deterministic side-effect-free numeric expression trees for gameplay values.
 * Connections: Used by effects, stats, conditions, balance data, and future admin formula builders.
 * Risk: High because formula behavior affects costs, damage, healing, scaling, and replay determinism.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Formulas;

public sealed class FormulaExpression
{
    public FormulaExpression(
        StableId operatorId,
        IEnumerable<FormulaExpression>? arguments = null,
        decimal? constantValue = null,
        StableId? variableId = null,
        TargetSelectorSpec? selector = null)
    {
        OperatorId = operatorId;
        Arguments = new ReadOnlyCollection<FormulaExpression>((arguments ?? Array.Empty<FormulaExpression>()).ToArray());
        ConstantValue = constantValue;
        VariableId = variableId;
        Selector = selector;
    }

    public StableId OperatorId { get; }
    public IReadOnlyList<FormulaExpression> Arguments { get; }
    public decimal? ConstantValue { get; }
    public StableId? VariableId { get; }
    public TargetSelectorSpec? Selector { get; }

    public static FormulaExpression Constant(decimal value) =>
        new(FormulaOperatorIds.Constant, constantValue: value);

    public static FormulaExpression Variable(StableId variableId) =>
        new(FormulaOperatorIds.Variable, variableId: variableId);

    public static FormulaExpression Call(StableId operatorId, params FormulaExpression[] arguments) =>
        new(operatorId, arguments);

    public static FormulaExpression Count(TargetSelectorSpec selector) =>
        new(FormulaOperatorIds.Count, selector: selector);
}

public static class FormulaOperatorIds
{
    public static readonly StableId Constant = StableId.Parse("formula.constant");
    public static readonly StableId Variable = StableId.Parse("formula.variable");
    public static readonly StableId Add = StableId.Parse("formula.add");
    public static readonly StableId Subtract = StableId.Parse("formula.subtract");
    public static readonly StableId Multiply = StableId.Parse("formula.multiply");
    public static readonly StableId Divide = StableId.Parse("formula.divide");
    public static readonly StableId Min = StableId.Parse("formula.min");
    public static readonly StableId Max = StableId.Parse("formula.max");
    public static readonly StableId Clamp = StableId.Parse("formula.clamp");
    public static readonly StableId Abs = StableId.Parse("formula.abs");
    public static readonly StableId Floor = StableId.Parse("formula.floor");
    public static readonly StableId Ceil = StableId.Parse("formula.ceil");
    public static readonly StableId Count = StableId.Parse("formula.count");
}

public enum FormulaPayloadKind
{
    None,
    Constant,
    Variable,
    Selector
}

public delegate decimal FormulaOperatorHandler(
    FormulaExpression expression,
    FormulaEvaluationContext context,
    FormulaEvaluator evaluator);

public sealed class FormulaOperatorRegistration : IRegistryEntry
{
    public FormulaOperatorRegistration(
        PrimitiveDescriptor descriptor,
        int minimumArguments,
        int maximumArguments,
        FormulaPayloadKind payloadKind,
        FormulaOperatorHandler handler)
    {
        if (minimumArguments < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumArguments));
        }

        if (maximumArguments < minimumArguments)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArguments));
        }

        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        MinimumArguments = minimumArguments;
        MaximumArguments = maximumArguments;
        PayloadKind = payloadKind;
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public StableId Id => Descriptor.Id;
    public PrimitiveDescriptor Descriptor { get; }
    public int MinimumArguments { get; }
    public int MaximumArguments { get; }
    public FormulaPayloadKind PayloadKind { get; }
    public FormulaOperatorHandler Handler { get; }
}

public sealed class FormulaOperatorRegistry : PrimitiveRegistry<FormulaOperatorRegistration>
{
    public FormulaOperatorRegistry(IEnumerable<FormulaOperatorRegistration>? registrations = null) : base(registrations)
    {
    }
}

public interface IFormulaVariableResolver
{
    decimal Resolve(StableId variableId);
}

public interface ITargetCountResolver
{
    int Count(TargetSelectorSpec selector);
}

public sealed class FormulaEvaluationContext
{
    public FormulaEvaluationContext(
        IFormulaVariableResolver variables,
        ITargetCountResolver targetCounts)
    {
        Variables = variables ?? throw new ArgumentNullException(nameof(variables));
        TargetCounts = targetCounts ?? throw new ArgumentNullException(nameof(targetCounts));
    }

    public IFormulaVariableResolver Variables { get; }
    public ITargetCountResolver TargetCounts { get; }
}

public sealed class FormulaEvaluator
{
    private readonly FormulaOperatorRegistry _operators;

    public FormulaEvaluator(FormulaOperatorRegistry operators)
    {
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
    }

    public decimal Evaluate(FormulaExpression expression, FormulaEvaluationContext context)
    {
        if (expression is null)
        {
            throw new ArgumentNullException(nameof(expression));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var registration = _operators.GetRequired(expression.OperatorId);
        ValidateArity(expression, registration);
        ValidatePayload(expression, registration.PayloadKind);
        return registration.Handler(expression, context, this);
    }

    private static void ValidateArity(
        FormulaExpression expression,
        FormulaOperatorRegistration registration)
    {
        var count = expression.Arguments.Count;
        if (count < registration.MinimumArguments || count > registration.MaximumArguments)
        {
            throw new InvalidOperationException(
                $"Operator '{registration.Id}' expects {registration.MinimumArguments}..{registration.MaximumArguments} arguments, got {count}.");
        }
    }

    private static void ValidatePayload(FormulaExpression expression, FormulaPayloadKind payloadKind)
    {
        var valid = payloadKind switch
        {
            FormulaPayloadKind.None => expression.ConstantValue is null && expression.VariableId is null && expression.Selector is null,
            FormulaPayloadKind.Constant => expression.ConstantValue is not null && expression.VariableId is null && expression.Selector is null,
            FormulaPayloadKind.Variable => expression.ConstantValue is null && expression.VariableId is not null && expression.Selector is null,
            FormulaPayloadKind.Selector => expression.ConstantValue is null && expression.VariableId is null && expression.Selector is not null,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException($"Operator '{expression.OperatorId}' has an invalid payload shape.");
        }
    }
}

public static class DefaultFormulaOperators
{
    public static FormulaOperatorRegistry Create()
    {
        return new FormulaOperatorRegistry(new[]
        {
            Register(FormulaOperatorIds.Constant, "formula.constant", "Literal decimal value.", 0, 0, FormulaPayloadKind.Constant, EvaluateConstant),
            Register(FormulaOperatorIds.Variable, "formula.variable", "Resolve a registered numeric gameplay variable.", 0, 0, FormulaPayloadKind.Variable, EvaluateVariable),
            Register(FormulaOperatorIds.Add, "formula.add", "Add two or more numeric expressions.", 2, int.MaxValue, FormulaPayloadKind.None, EvaluateAdd),
            Register(FormulaOperatorIds.Subtract, "formula.subtract", "Subtract the second expression from the first.", 2, 2, FormulaPayloadKind.None, EvaluateSubtract),
            Register(FormulaOperatorIds.Multiply, "formula.multiply", "Multiply two or more numeric expressions.", 2, int.MaxValue, FormulaPayloadKind.None, EvaluateMultiply),
            Register(FormulaOperatorIds.Divide, "formula.divide", "Divide the first expression by the second.", 2, 2, FormulaPayloadKind.None, EvaluateDivide),
            Register(FormulaOperatorIds.Min, "formula.min", "Return the minimum of two or more expressions.", 2, int.MaxValue, FormulaPayloadKind.None, EvaluateMin),
            Register(FormulaOperatorIds.Max, "formula.max", "Return the maximum of two or more expressions.", 2, int.MaxValue, FormulaPayloadKind.None, EvaluateMax),
            Register(FormulaOperatorIds.Clamp, "formula.clamp", "Clamp value between minimum and maximum.", 3, 3, FormulaPayloadKind.None, EvaluateClamp),
            Register(FormulaOperatorIds.Abs, "formula.abs", "Return the absolute value.", 1, 1, FormulaPayloadKind.None, EvaluateAbs),
            Register(FormulaOperatorIds.Floor, "formula.floor", "Round down to the nearest integer value.", 1, 1, FormulaPayloadKind.None, EvaluateFloor),
            Register(FormulaOperatorIds.Ceil, "formula.ceil", "Round up to the nearest integer value.", 1, 1, FormulaPayloadKind.None, EvaluateCeil),
            Register(FormulaOperatorIds.Count, "formula.count", "Count entities resolved by a target selector.", 0, 0, FormulaPayloadKind.Selector, EvaluateCount)
        });
    }

    private static FormulaOperatorRegistration Register(
        StableId id,
        string labelKey,
        string description,
        int minArgs,
        int maxArgs,
        FormulaPayloadKind payloadKind,
        FormulaOperatorHandler handler)
    {
        var descriptor = new PrimitiveDescriptor(id, labelKey, description);
        return new FormulaOperatorRegistration(descriptor, minArgs, maxArgs, payloadKind, handler);
    }

    private static decimal EvaluateConstant(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator) => expression.ConstantValue!.Value;

    private static decimal EvaluateVariable(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator) => context.Variables.Resolve(expression.VariableId!.Value);

    private static decimal EvaluateAdd(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator)
    {
        decimal result = 0;
        foreach (var argument in expression.Arguments)
        {
            result += evaluator.Evaluate(argument, context);
        }

        return result;
    }

    private static decimal EvaluateSubtract(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator) =>
        evaluator.Evaluate(expression.Arguments[0], context) - evaluator.Evaluate(expression.Arguments[1], context);

    private static decimal EvaluateMultiply(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator)
    {
        decimal result = 1;
        foreach (var argument in expression.Arguments)
        {
            result *= evaluator.Evaluate(argument, context);
        }

        return result;
    }

    private static decimal EvaluateDivide(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator)
    {
        var denominator = evaluator.Evaluate(expression.Arguments[1], context);
        if (denominator == 0)
        {
            throw new DivideByZeroException("Gameplay formulas cannot divide by zero.");
        }

        return evaluator.Evaluate(expression.Arguments[0], context) / denominator;
    }

    private static decimal EvaluateMin(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator)
    {
        var result = evaluator.Evaluate(expression.Arguments[0], context);
        for (var index = 1; index < expression.Arguments.Count; index += 1)
        {
            result = Math.Min(result, evaluator.Evaluate(expression.Arguments[index], context));
        }

        return result;
    }

    private static decimal EvaluateMax(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator)
    {
        var result = evaluator.Evaluate(expression.Arguments[0], context);
        for (var index = 1; index < expression.Arguments.Count; index += 1)
        {
            result = Math.Max(result, evaluator.Evaluate(expression.Arguments[index], context));
        }

        return result;
    }

    private static decimal EvaluateClamp(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator)
    {
        var value = evaluator.Evaluate(expression.Arguments[0], context);
        var minimum = evaluator.Evaluate(expression.Arguments[1], context);
        var maximum = evaluator.Evaluate(expression.Arguments[2], context);
        if (minimum > maximum)
        {
            throw new InvalidOperationException("Clamp minimum cannot exceed maximum.");
        }

        return Math.Min(maximum, Math.Max(minimum, value));
    }

    private static decimal EvaluateAbs(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator) => Math.Abs(evaluator.Evaluate(expression.Arguments[0], context));

    private static decimal EvaluateFloor(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator) => Math.Floor(evaluator.Evaluate(expression.Arguments[0], context));

    private static decimal EvaluateCeil(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator) => Math.Ceiling(evaluator.Evaluate(expression.Arguments[0], context));

    private static decimal EvaluateCount(
        FormulaExpression expression,
        FormulaEvaluationContext context,
        FormulaEvaluator evaluator) => context.TargetCounts.Count(expression.Selector!);
}
