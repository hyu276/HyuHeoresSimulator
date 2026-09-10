/**
 * AUTHORING_PARAMETERS
 * Purpose: Defines safe typed parameter values and metadata used by schema-driven authoring tools.
 * Connections: Primitive descriptors validate effect, condition, trigger, duration, and limit configurations.
 * Risk: High because weak parameter typing could permit invalid or ambiguous gameplay content.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Conditions;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Effects;
using HyuHeroes.Gameplay.Formulas;
using HyuHeroes.Gameplay.Selectors;

namespace HyuHeroes.Gameplay.Authoring;

public enum ParameterKind
{
    Integer,
    Decimal,
    Boolean,
    String,
    StableId,
    Enum,
    Formula,
    Selector,
    Condition,
    EffectList
}

public abstract class ParameterValue
{
    protected ParameterValue(ParameterKind kind) => Kind = kind;

    public ParameterKind Kind { get; }
}

public sealed class IntegerParameterValue : ParameterValue
{
    public IntegerParameterValue(int value) : base(ParameterKind.Integer) => Value = value;
    public int Value { get; }
}

public sealed class DecimalParameterValue : ParameterValue
{
    public DecimalParameterValue(decimal value) : base(ParameterKind.Decimal) => Value = value;
    public decimal Value { get; }
}

public sealed class BooleanParameterValue : ParameterValue
{
    public BooleanParameterValue(bool value) : base(ParameterKind.Boolean) => Value = value;
    public bool Value { get; }
}

public sealed class StringParameterValue : ParameterValue
{
    public StringParameterValue(string value) : base(ParameterKind.String)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value { get; }
}

public sealed class StableIdParameterValue : ParameterValue
{
    public StableIdParameterValue(StableId value) : base(ParameterKind.StableId) => Value = value;
    public StableId Value { get; }
}

public sealed class EnumParameterValue : ParameterValue
{
    public EnumParameterValue(string value) : base(ParameterKind.Enum)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Enum value cannot be empty.", nameof(value))
            : value;
    }

    public string Value { get; }
}

public sealed class FormulaParameterValue : ParameterValue
{
    public FormulaParameterValue(FormulaExpression value) : base(ParameterKind.Formula)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public FormulaExpression Value { get; }
}

public sealed class SelectorParameterValue : ParameterValue
{
    public SelectorParameterValue(TargetSelectorSpec value) : base(ParameterKind.Selector)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public TargetSelectorSpec Value { get; }
}

public sealed class ConditionParameterValue : ParameterValue
{
    public ConditionParameterValue(ConditionNode value) : base(ParameterKind.Condition)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ConditionNode Value { get; }
}

public sealed class EffectListParameterValue : ParameterValue
{
    public EffectListParameterValue(IEnumerable<EffectDefinition> value) : base(ParameterKind.EffectList)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        Value = new ReadOnlyCollection<EffectDefinition>(value.ToArray());
    }

    public IReadOnlyList<EffectDefinition> Value { get; }
}

public enum RegistryReferenceKind
{
    None,
    Tag,
    Keyword,
    Stat,
    Resource,
    Duration,
    CardDefinition,
    Modifier,
    Phase
}

public sealed class ParameterSchema
{
    public ParameterSchema(
        string name,
        ParameterKind kind,
        bool required = true,
        decimal? minimum = null,
        decimal? maximum = null,
        IEnumerable<string>? allowedValues = null,
        string? editorHint = null,
        RegistryReferenceKind referenceKind = RegistryReferenceKind.None)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Parameter name cannot be empty.", nameof(name))
            : name;
        Kind = kind;
        Required = required;
        Minimum = minimum;
        Maximum = maximum;
        AllowedValues = new ReadOnlyCollection<string>((allowedValues ?? Array.Empty<string>()).ToArray());
        EditorHint = editorHint;
        ReferenceKind = referenceKind;
    }

    public string Name { get; }
    public ParameterKind Kind { get; }
    public bool Required { get; }
    public decimal? Minimum { get; }
    public decimal? Maximum { get; }
    public IReadOnlyList<string> AllowedValues { get; }
    public string? EditorHint { get; }
    public RegistryReferenceKind ReferenceKind { get; }
}

public sealed class ParameterBag
{
    private readonly IReadOnlyDictionary<string, ParameterValue> _values;

    public ParameterBag(IEnumerable<KeyValuePair<string, ParameterValue>>? values = null)
    {
        var dictionary = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var pair in values ?? Array.Empty<KeyValuePair<string, ParameterValue>>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException("Parameter names cannot be empty.", nameof(values));
            }

            dictionary.Add(pair.Key, pair.Value ?? throw new ArgumentException("Parameter values cannot be null.", nameof(values)));
        }

        _values = new ReadOnlyDictionary<string, ParameterValue>(dictionary);
    }

    public IReadOnlyDictionary<string, ParameterValue> Values => _values;

    public bool TryGet(string name, out ParameterValue? value) => _values.TryGetValue(name, out value);

    public T GetRequired<T>(string name) where T : ParameterValue
    {
        if (!_values.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException($"Missing required parameter '{name}'.");
        }

        return value as T
            ?? throw new InvalidOperationException($"Parameter '{name}' is not {typeof(T).Name}.");
    }
}

public sealed class PrimitiveDescriptor
{
    public PrimitiveDescriptor(
        StableId id,
        string labelKey,
        string description,
        IEnumerable<ParameterSchema>? parameters = null,
        bool deprecated = false)
    {
        Id = id;
        LabelKey = string.IsNullOrWhiteSpace(labelKey)
            ? throw new ArgumentException("Label key cannot be empty.", nameof(labelKey))
            : labelKey;
        Description = description ?? throw new ArgumentNullException(nameof(description));
        var parameterArray = (parameters ?? Array.Empty<ParameterSchema>()).ToArray();
        if (parameterArray.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Primitive parameter names must be unique.", nameof(parameters));
        }

        Parameters = new ReadOnlyCollection<ParameterSchema>(parameterArray);
        Deprecated = deprecated;
    }

    public StableId Id { get; }
    public string LabelKey { get; }
    public string Description { get; }
    public IReadOnlyList<ParameterSchema> Parameters { get; }
    public bool Deprecated { get; }
}
