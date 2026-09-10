/**
 * CONDITION_TREE
 * Purpose: Represents deterministic nested boolean logic with registered leaf predicate configurations.
 * Connections: Used by abilities, target filters, effect branches, validation, and future admin builders.
 * Risk: High because condition semantics decide whether authoritative gameplay operations are legally applied.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Authoring;
using HyuHeroes.Gameplay.Core;

namespace HyuHeroes.Gameplay.Conditions;

public enum ConditionNodeKind
{
    All,
    Any,
    Not,
    Predicate
}

public abstract class ConditionNode
{
    protected ConditionNode(ConditionNodeKind kind) => Kind = kind;

    public ConditionNodeKind Kind { get; }
}

public sealed class AllConditionNode : ConditionNode
{
    public AllConditionNode(IEnumerable<ConditionNode> children) : base(ConditionNodeKind.All)
    {
        Children = CreateChildren(children, nameof(children));
    }

    public IReadOnlyList<ConditionNode> Children { get; }

    private static IReadOnlyList<ConditionNode> CreateChildren(IEnumerable<ConditionNode> children, string parameterName)
    {
        if (children is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var values = children.ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("ALL requires at least one child.", parameterName);
        }

        if (values.Any(value => value is null))
        {
            throw new ArgumentException("ALL children cannot contain null nodes.", parameterName);
        }

        return new ReadOnlyCollection<ConditionNode>(values);
    }
}

public sealed class AnyConditionNode : ConditionNode
{
    public AnyConditionNode(IEnumerable<ConditionNode> children) : base(ConditionNodeKind.Any)
    {
        if (children is null)
        {
            throw new ArgumentNullException(nameof(children));
        }

        var values = children.ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("ANY requires at least one child.", nameof(children));
        }

        if (values.Any(value => value is null))
        {
            throw new ArgumentException("ANY children cannot contain null nodes.", nameof(children));
        }

        Children = new ReadOnlyCollection<ConditionNode>(values);
    }

    public IReadOnlyList<ConditionNode> Children { get; }
}

public sealed class NotConditionNode : ConditionNode
{
    public NotConditionNode(ConditionNode child) : base(ConditionNodeKind.Not)
    {
        Child = child ?? throw new ArgumentNullException(nameof(child));
    }

    public ConditionNode Child { get; }
}

public sealed class PredicateConditionNode : ConditionNode
{
    public PredicateConditionNode(ConditionPredicateSpec predicate) : base(ConditionNodeKind.Predicate)
    {
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    public ConditionPredicateSpec Predicate { get; }
}

public sealed class ConditionPredicateSpec
{
    public ConditionPredicateSpec(StableId typeId, ParameterBag? parameters = null)
    {
        TypeId = typeId;
        Parameters = parameters ?? new ParameterBag();
    }

    public StableId TypeId { get; }
    public ParameterBag Parameters { get; }
}

public interface IConditionPredicateResolver
{
    bool Evaluate(ConditionPredicateSpec predicate);
}

public sealed class ConditionEvaluator
{
    public bool Evaluate(ConditionNode node, IConditionPredicateResolver predicateResolver)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (predicateResolver is null)
        {
            throw new ArgumentNullException(nameof(predicateResolver));
        }

        return EvaluateNode(node, predicateResolver);
    }

    private bool EvaluateNode(ConditionNode node, IConditionPredicateResolver predicateResolver)
    {
        return node switch
        {
            AllConditionNode all => EvaluateAll(all, predicateResolver),
            AnyConditionNode any => EvaluateAny(any, predicateResolver),
            NotConditionNode not => !EvaluateNode(not.Child, predicateResolver),
            PredicateConditionNode predicate => predicateResolver.Evaluate(predicate.Predicate),
            _ => throw new InvalidOperationException($"Unsupported condition node '{node.GetType().Name}'.")
        };
    }

    private bool EvaluateAll(AllConditionNode node, IConditionPredicateResolver predicateResolver)
    {
        foreach (var child in node.Children)
        {
            if (!EvaluateNode(child, predicateResolver))
            {
                return false;
            }
        }

        return true;
    }

    private bool EvaluateAny(AnyConditionNode node, IConditionPredicateResolver predicateResolver)
    {
        foreach (var child in node.Children)
        {
            if (EvaluateNode(child, predicateResolver))
            {
                return true;
            }
        }

        return false;
    }
}
