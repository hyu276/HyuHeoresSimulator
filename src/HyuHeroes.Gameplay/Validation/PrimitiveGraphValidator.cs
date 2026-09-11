/**
 * PRIMITIVE_GRAPH_VALIDATOR
 * Purpose: Validates effects, parameters, formulas, conditions, selectors, and controlled primitive references recursively.
 * Connections: Used by GameplaySchemaValidator against metadata stored in GameplayRegistryCatalog.
 * Risk: High because malformed nested authoring graphs must never reach authoritative execution.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Validation;

internal sealed class PrimitiveGraphValidator
{
    private readonly GameplayRegistryCatalog _catalog;

    public PrimitiveGraphValidator(GameplayRegistryCatalog catalog) => _catalog = catalog;

    public void ValidateEffect(EffectDefinition effect, string path, int depth, ValidationCollector errors)
    {
        if (effect is null)
        {
            errors.Add("effect.null_node", path, "Effect graph cannot contain null nodes.");
            return;
        }

        if (depth > GameplaySchemaValidator.MaxEffectDepth)
        {
            errors.Add("effect.depth", path, $"Effect nesting depth exceeds {GameplaySchemaValidator.MaxEffectDepth}.");
            return;
        }

        if (!_catalog.Effects.TryGet(effect.TypeId, out var registration) || registration is null)
        {
            errors.Add("effect.unknown_type", $"{path}.typeId", $"Unknown effect '{effect.TypeId}'.");
            return;
        }

        ValidateParameterBag(registration.Descriptor, effect.Parameters, path, errors);
        ValidateModifierDurationParameters(effect, path, errors);
        ValidateNestedEffects(effect.Parameters, path, depth, errors);
    }

    public void ValidateFormula(FormulaExpression expression, string path, int depth, ValidationCollector errors)
    {
        if (expression is null)
        {
            errors.Add("formula.null_node", path, "Formula graph cannot contain null nodes.");
            return;
        }

        if (depth > GameplaySchemaValidator.MaxFormulaDepth)
        {
            errors.Add("formula.depth", path, $"Formula depth exceeds {GameplaySchemaValidator.MaxFormulaDepth}.");
            return;
        }

        if (!_catalog.FormulaOperators.TryGet(expression.OperatorId, out var registration) || registration is null)
        {
            errors.Add("formula.unknown_operator", $"{path}.operatorId", $"Unknown formula operator '{expression.OperatorId}'.");
            return;
        }

        ValidateFormulaShape(expression, registration, path, errors);
        for (var index = 0; index < expression.Arguments.Count; index += 1)
        {
            ValidateFormula(expression.Arguments[index], $"{path}.arguments[{index}]", depth + 1, errors);
        }
    }

    public void ValidateCondition(ConditionNode node, string path, int depth, ValidationCollector errors)
    {
        if (node is null)
        {
            errors.Add("condition.null_node", path, "Condition graph cannot contain null nodes.");
            return;
        }

        if (depth > GameplaySchemaValidator.MaxConditionDepth)
        {
            errors.Add("condition.depth", path, $"Condition depth exceeds {GameplaySchemaValidator.MaxConditionDepth}.");
            return;
        }

        switch (node)
        {
            case AllConditionNode all:
                ValidateConditionChildren(all.Children, path, depth, errors);
                return;
            case AnyConditionNode any:
                ValidateConditionChildren(any.Children, path, depth, errors);
                return;
            case NotConditionNode not:
                ValidateCondition(not.Child, $"{path}.child", depth + 1, errors);
                return;
            case PredicateConditionNode predicate:
                ValidateDescriptor(_catalog.Conditions, predicate.Predicate.TypeId, predicate.Predicate.Parameters, $"{path}.predicate", errors);
                return;
            default:
                errors.Add("condition.unknown_node", path, $"Unsupported condition node '{node.GetType().Name}'.");
                return;
        }
    }

    public void ValidateDescriptor(
        PrimitiveRegistry<DescriptorRegistration> registry,
        StableId typeId,
        ParameterBag parameters,
        string path,
        ValidationCollector errors)
    {
        if (!registry.TryGet(typeId, out var registration) || registration is null)
        {
            errors.Add("primitive.unknown_type", $"{path}.typeId", $"Unknown primitive '{typeId}'.");
            return;
        }

        ValidateParameterBag(registration.Descriptor, parameters, path, errors);
    }

    private void ValidateParameterBag(
        PrimitiveDescriptor descriptor,
        ParameterBag bag,
        string path,
        ValidationCollector errors)
    {
        var schemas = descriptor.Parameters.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var pair in bag.Values.Where(pair => !schemas.ContainsKey(pair.Key)))
        {
            errors.Add("parameter.unknown", $"{path}.parameters.{pair.Key}", "Parameter is not declared by this primitive.");
        }

        foreach (var schema in descriptor.Parameters)
        {
            ValidateParameter(schema, bag, path, errors);
        }
    }

    private void ValidateParameter(
        ParameterSchema schema,
        ParameterBag bag,
        string path,
        ValidationCollector errors)
    {
        var parameterPath = $"{path}.parameters.{schema.Name}";
        if (!bag.TryGet(schema.Name, out var value) || value is null)
        {
            if (schema.Required)
            {
                errors.Add("parameter.required", parameterPath, "Required parameter is missing.");
            }
            return;
        }

        if (value.Kind != schema.Kind)
        {
            errors.Add("parameter.kind", parameterPath, $"Expected {schema.Kind}, got {value.Kind}.");
            return;
        }

        ValidateTypedValue(schema, value, parameterPath, errors);
    }

    private void ValidateTypedValue(
        ParameterSchema schema,
        ParameterValue value,
        string path,
        ValidationCollector errors)
    {
        if (value is IntegerParameterValue integerValue)
        {
            ValidateRange(schema, integerValue.Value, path, errors);
            return;
        }

        if (value is DecimalParameterValue decimalValue)
        {
            ValidateRange(schema, decimalValue.Value, path, errors);
            return;
        }

        ValidateStructuredValue(schema, value, path, errors);
    }

    private void ValidateStructuredValue(
        ParameterSchema schema,
        ParameterValue value,
        string path,
        ValidationCollector errors)
    {
        switch (value)
        {
            case EnumParameterValue enumValue:
                if (schema.AllowedValues.Count > 0 && !schema.AllowedValues.Contains(enumValue.Value, StringComparer.Ordinal))
                {
                    errors.Add("parameter.enum", path, $"Value '{enumValue.Value}' is not allowed.");
                }
                return;
            case StableIdParameterValue stableIdValue:
                ValidateReference(schema.ReferenceKind, stableIdValue.Value, path, errors);
                return;
            case FormulaParameterValue formulaValue:
                ValidateFormula(formulaValue.Value, path, 1, errors);
                return;
            case SelectorParameterValue selectorValue:
                ValidateSelector(selectorValue.Value, path, errors);
                return;
            case ConditionParameterValue conditionValue:
                ValidateCondition(conditionValue.Value, path, 1, errors);
                return;
        }
    }

    private void ValidateModifierDurationParameters(EffectDefinition effect, string path, ValidationCollector errors)
    {
        if (effect.TypeId != EffectIds.ModifyStat ||
            !effect.Parameters.TryGet("durationId", out var durationValue) ||
            durationValue is not StableIdParameterValue durationIdValue)
        {
            return;
        }

        var durationId = durationIdValue.Value;
        var hasTurns = effect.Parameters.TryGet("durationTurns", out _);
        var hasZone = effect.Parameters.TryGet("durationZone", out _);
        if (durationId == DurationIds.ForNTurns && !hasTurns)
        {
            errors.Add(
                "effect.duration_parameter_required",
                $"{path}.parameters.durationTurns",
                "duration.for_n_turns requires durationTurns.");
        }
        else if (durationId != DurationIds.ForNTurns && hasTurns)
        {
            errors.Add(
                "effect.duration_parameter_invalid",
                $"{path}.parameters.durationTurns",
                "durationTurns is only valid with duration.for_n_turns.");
        }

        if (durationId == DurationIds.WhileInZone && !hasZone)
        {
            errors.Add(
                "effect.duration_parameter_required",
                $"{path}.parameters.durationZone",
                "duration.while_in_zone requires durationZone.");
        }
        else if (durationId != DurationIds.WhileInZone && hasZone)
        {
            errors.Add(
                "effect.duration_parameter_invalid",
                $"{path}.parameters.durationZone",
                "durationZone is only valid with duration.while_in_zone.");
        }
    }

    private void ValidateNestedEffects(ParameterBag parameters, string path, int depth, ValidationCollector errors)
    {
        foreach (var pair in parameters.Values)
        {
            if (pair.Value is not EffectListParameterValue effectList)
            {
                continue;
            }

            if (effectList.Value.Count == 0)
            {
                errors.Add("effect.empty_list", $"{path}.parameters.{pair.Key}", "Effect list must not be empty when provided.");
                continue;
            }

            for (var index = 0; index < effectList.Value.Count; index += 1)
            {
                ValidateEffect(effectList.Value[index], $"{path}.parameters.{pair.Key}[{index}]", depth + 1, errors);
            }
        }
    }

    private void ValidateFormulaShape(
        FormulaExpression expression,
        FormulaOperatorRegistration registration,
        string path,
        ValidationCollector errors)
    {
        var count = expression.Arguments.Count;
        if (count < registration.MinimumArguments || count > registration.MaximumArguments)
        {
            errors.Add("formula.arity", $"{path}.arguments", $"Expected {registration.MinimumArguments}..{registration.MaximumArguments} arguments, got {count}.");
        }

        if (!HasPayload(expression, registration.PayloadKind))
        {
            errors.Add("formula.payload", path, $"Operator '{expression.OperatorId}' has an invalid payload shape.");
            return;
        }

        ValidateFormulaPayload(expression, registration.PayloadKind, path, errors);
    }

    private void ValidateFormulaPayload(
        FormulaExpression expression,
        FormulaPayloadKind payloadKind,
        string path,
        ValidationCollector errors)
    {
        if (payloadKind == FormulaPayloadKind.Variable && expression.VariableId is { } variableId)
        {
            RequireRegistered(_catalog.FormulaVariables, variableId, $"{path}.variableId", "formula variable", errors);
            return;
        }

        if (payloadKind != FormulaPayloadKind.Selector || expression.Selector is null)
        {
            return;
        }

        ValidateSelector(expression.Selector, $"{path}.selector", errors);
        if (expression.OperatorId == FormulaOperatorIds.Count && expression.Selector.Selection != TargetSelection.All)
        {
            errors.Add("formula.count_selector", $"{path}.selector.selection", "COUNT requires ALL selection and cannot consume RNG or player choice.");
        }
    }

    private static bool HasPayload(FormulaExpression expression, FormulaPayloadKind payloadKind) =>
        payloadKind switch
        {
            FormulaPayloadKind.None => expression.ConstantValue is null && expression.VariableId is null && expression.Selector is null,
            FormulaPayloadKind.Constant => expression.ConstantValue is not null && expression.VariableId is null && expression.Selector is null,
            FormulaPayloadKind.Variable => expression.ConstantValue is null && expression.VariableId is not null && expression.Selector is null,
            FormulaPayloadKind.Selector => expression.ConstantValue is null && expression.VariableId is null && expression.Selector is not null,
            _ => false
        };

    private void ValidateSelector(TargetSelectorSpec selector, string path, ValidationCollector errors)
    {
        if (selector.Selection != TargetSelection.RandomN && selector.SelectionCount != 1)
        {
            errors.Add("selector.selection_count", $"{path}.selectionCount", "Only RANDOM_N may currently use selectionCount other than 1.");
        }

        for (var index = 0; index < selector.Filters.Count; index += 1)
        {
            ValidateCondition(selector.Filters[index], $"{path}.filters[{index}]", 1, errors);
        }
    }

    private void ValidateConditionChildren(
        IReadOnlyList<ConditionNode> children,
        string path,
        int depth,
        ValidationCollector errors)
    {
        for (var index = 0; index < children.Count; index += 1)
        {
            ValidateCondition(children[index], $"{path}.children[{index}]", depth + 1, errors);
        }
    }

    private static void ValidateRange(ParameterSchema schema, decimal value, string path, ValidationCollector errors)
    {
        if (schema.Minimum is { } minimum && value < minimum)
        {
            errors.Add("parameter.minimum", path, $"Value {value} is below minimum {minimum}.");
        }

        if (schema.Maximum is { } maximum && value > maximum)
        {
            errors.Add("parameter.maximum", path, $"Value {value} exceeds maximum {maximum}.");
        }
    }

    private void ValidateReference(RegistryReferenceKind kind, StableId value, string path, ValidationCollector errors)
    {
        switch (kind)
        {
            case RegistryReferenceKind.Tag:
                RequireRegistered(_catalog.Tags, value, path, "tag", errors);
                return;
            case RegistryReferenceKind.Keyword:
                RequireRegistered(_catalog.Keywords, value, path, "keyword", errors);
                return;
            case RegistryReferenceKind.Stat:
                RequireRegistered(_catalog.Stats, value, path, "stat", errors);
                return;
            case RegistryReferenceKind.Resource:
                RequireRegistered(_catalog.Resources, value, path, "resource", errors);
                return;
            default:
                ValidateSecondaryReference(kind, value, path, errors);
                return;
        }
    }

    private void ValidateSecondaryReference(RegistryReferenceKind kind, StableId value, string path, ValidationCollector errors)
    {
        switch (kind)
        {
            case RegistryReferenceKind.Duration:
                RequireRegistered(_catalog.Durations, value, path, "duration", errors);
                return;
            case RegistryReferenceKind.Phase:
                RequireRegistered(_catalog.Phases, value, path, "phase", errors);
                return;
            case RegistryReferenceKind.None:
            case RegistryReferenceKind.CardDefinition:
            case RegistryReferenceKind.Modifier:
                return;
            default:
                errors.Add("parameter.reference_kind", path, $"Unsupported reference kind '{kind}'.");
                return;
        }
    }

    private static void RequireRegistered<TEntry>(
        PrimitiveRegistry<TEntry> registry,
        StableId id,
        string path,
        string kind,
        ValidationCollector errors)
        where TEntry : IRegistryEntry
    {
        if (!registry.Contains(id))
        {
            errors.Add("registry.unknown_id", path, $"Unknown {kind} ID '{id}'.");
        }
    }
}
