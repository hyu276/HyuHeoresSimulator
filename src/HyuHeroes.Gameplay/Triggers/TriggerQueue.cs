/**
 * TRIGGER_QUEUE
 * Purpose: Owns deterministic ordering for trigger work while preserving the immutable domain event that opened each reaction window.
 * Connections: Receives TriggerQueueItems from TriggerDiscovery and feeds the ability-resolution loop with event payload plus binding context.
 * Risk: High because queue ordering and event context determine replay-visible chained effect behavior.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Events;

namespace HyuHeroes.Gameplay.Triggers;

public sealed class TriggerBinding
{
    public TriggerBinding(
        StableId bindingId,
        StableId sourceId,
        StableId abilityId,
        StableId triggerId,
        int priority = 0)
    {
        if (bindingId == default || sourceId == default || abilityId == default || triggerId == default)
        {
            throw new ArgumentException("Trigger binding IDs must be non-default StableIds.");
        }

        BindingId = bindingId;
        SourceId = sourceId;
        AbilityId = abilityId;
        TriggerId = triggerId;
        Priority = priority;
    }

    public StableId BindingId { get; }
    public StableId SourceId { get; }
    public StableId AbilityId { get; }
    public StableId TriggerId { get; }
    public int Priority { get; }
}

public sealed class TriggerQueueItem
{
    public TriggerQueueItem(DomainEvent originatingEvent, int windowOrder, TriggerBinding binding)
    {
        OriginatingEvent = originatingEvent ?? throw new ArgumentNullException(nameof(originatingEvent));
        if (windowOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowOrder), "Trigger window order cannot be negative.");
        }

        WindowOrder = windowOrder;
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public DomainEvent OriginatingEvent { get; }
    public long EventSequence => OriginatingEvent.Sequence;
    public int WindowOrder { get; }
    public TriggerBinding Binding { get; }
}

public sealed class DeterministicTriggerQueue
{
    private readonly List<TriggerQueueItem> _items = new();

    public int Count => _items.Count;

    public void Enqueue(TriggerQueueItem item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (_items.Any(existing =>
            existing.EventSequence == item.EventSequence &&
            existing.Binding.BindingId == item.Binding.BindingId))
        {
            throw new InvalidOperationException(
                $"Trigger binding '{item.Binding.BindingId}' is already queued for event {item.EventSequence}.");
        }

        _items.Add(item);
        _items.Sort(Compare);
    }

    public void EnqueueRange(IEnumerable<TriggerQueueItem> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        foreach (var item in items)
        {
            Enqueue(item);
        }
    }

    public TriggerQueueItem Dequeue()
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("Trigger queue is empty.");
        }

        var item = _items[0];
        _items.RemoveAt(0);
        return item;
    }

    public IReadOnlyList<TriggerQueueItem> Snapshot() =>
        new ReadOnlyCollection<TriggerQueueItem>(_items.ToArray());

    private static int Compare(TriggerQueueItem left, TriggerQueueItem right)
    {
        var comparison = left.EventSequence.CompareTo(right.EventSequence);
        if (comparison != 0) return comparison;
        comparison = left.WindowOrder.CompareTo(right.WindowOrder);
        if (comparison != 0) return comparison;
        comparison = right.Binding.Priority.CompareTo(left.Binding.Priority);
        if (comparison != 0) return comparison;
        comparison = left.Binding.SourceId.CompareTo(right.Binding.SourceId);
        if (comparison != 0) return comparison;
        comparison = left.Binding.AbilityId.CompareTo(right.Binding.AbilityId);
        return comparison != 0 ? comparison : left.Binding.BindingId.CompareTo(right.Binding.BindingId);
    }
}
