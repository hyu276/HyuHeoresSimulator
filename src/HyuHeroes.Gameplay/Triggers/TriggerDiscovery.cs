/**
 * TRIGGER_DISCOVERY
 * Purpose: Converts ordered domain events into eligible trigger queue items while preserving the originating immutable event payload.
 * Connections: Reads TriggerBindings and DomainEvents, then populates DeterministicTriggerQueue for the ability-resolution loop.
 * Risk: High because trigger-window membership and event context decide which abilities may react and what they may reference.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Events;
using HyuHeroes.Gameplay.Registries;

namespace HyuHeroes.Gameplay.Triggers;

public sealed class TriggerDiscovery
{
    private const int DamageDealtWindowOrder = 0;
    private const int DamagedWindowOrder = 1;
    private const int DeathWindowOrder = 0;
    private readonly IReadOnlyList<TriggerBinding> _bindings;

    public TriggerDiscovery(IEnumerable<TriggerBinding> bindings)
    {
        if (bindings is null)
        {
            throw new ArgumentNullException(nameof(bindings));
        }

        var bindingArray = bindings.ToArray();
        if (bindingArray.Any(binding => binding is null))
        {
            throw new ArgumentException("Trigger bindings cannot contain null values.", nameof(bindings));
        }

        if (bindingArray.GroupBy(binding => binding.BindingId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Trigger binding IDs must be unique.", nameof(bindings));
        }

        _bindings = new ReadOnlyCollection<TriggerBinding>(bindingArray);
    }

    public IReadOnlyList<TriggerQueueItem> Discover(DomainEvent domainEvent)
    {
        if (domainEvent is null)
        {
            throw new ArgumentNullException(nameof(domainEvent));
        }

        return domainEvent switch
        {
            DamageAppliedDomainEvent damage => DiscoverDamage(damage),
            EntityDiedDomainEvent death => DiscoverDeath(death),
            _ => Array.Empty<TriggerQueueItem>()
        };
    }

    public void DiscoverInto(IEnumerable<DomainEvent> events, DeterministicTriggerQueue queue)
    {
        if (events is null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        if (queue is null)
        {
            throw new ArgumentNullException(nameof(queue));
        }

        foreach (var domainEvent in events.OrderBy(item => item.Sequence))
        {
            queue.EnqueueRange(Discover(domainEvent));
        }
    }

    private IReadOnlyList<TriggerQueueItem> DiscoverDamage(DamageAppliedDomainEvent damage)
    {
        if (damage.Resolution.HealthLost <= 0m)
        {
            return Array.Empty<TriggerQueueItem>();
        }

        var items = new List<TriggerQueueItem>();
        items.AddRange(CreateItems(damage, DamageDealtWindowOrder, TriggerIds.OnDamageDealt, damage.SourceId));
        if (damage.TargetId is { } targetId)
        {
            items.AddRange(CreateItems(damage, DamagedWindowOrder, TriggerIds.OnDamaged, targetId));
        }

        return new ReadOnlyCollection<TriggerQueueItem>(items);
    }

    private IReadOnlyList<TriggerQueueItem> DiscoverDeath(EntityDiedDomainEvent death) =>
        new ReadOnlyCollection<TriggerQueueItem>(
            CreateItems(death, DeathWindowOrder, TriggerIds.OnDeath, death.SourceId).ToArray());

    private IEnumerable<TriggerQueueItem> CreateItems(
        DomainEvent originatingEvent,
        int windowOrder,
        StableId triggerId,
        StableId sourceId) =>
        _bindings
            .Where(binding => binding.TriggerId == triggerId && binding.SourceId == sourceId)
            .Select(binding => new TriggerQueueItem(originatingEvent, windowOrder, binding));
}
