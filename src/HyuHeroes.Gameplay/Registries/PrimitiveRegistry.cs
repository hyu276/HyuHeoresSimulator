/**
 * PRIMITIVE_REGISTRY
 * Purpose: Supplies immutable lookup registries for controlled gameplay taxonomy and authorable primitives.
 * Connections: Used by content validation, formula evaluation, admin metadata export, and schema composition.
 * Risk: High because registry identity and duplicate handling define the legal gameplay vocabulary.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;

namespace HyuHeroes.Gameplay.Registries;

public interface IRegistryEntry
{
    StableId Id { get; }
}

public sealed class RegistryEntry : IRegistryEntry
{
    public RegistryEntry(StableId id, string labelKey, string description)
    {
        Id = id;
        LabelKey = string.IsNullOrWhiteSpace(labelKey)
            ? throw new ArgumentException("Label key cannot be empty.", nameof(labelKey))
            : labelKey;
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public StableId Id { get; }
    public string LabelKey { get; }
    public string Description { get; }
}

public class PrimitiveRegistry<TEntry> where TEntry : IRegistryEntry
{
    private readonly IReadOnlyDictionary<StableId, TEntry> _entries;

    public PrimitiveRegistry(IEnumerable<TEntry>? entries = null)
    {
        var dictionary = new Dictionary<StableId, TEntry>();
        foreach (var entry in entries ?? Array.Empty<TEntry>())
        {
            if (dictionary.ContainsKey(entry.Id))
            {
                throw new ArgumentException($"Duplicate registry ID '{entry.Id}'.", nameof(entries));
            }

            dictionary.Add(entry.Id, entry);
        }

        _entries = new ReadOnlyDictionary<StableId, TEntry>(dictionary);
    }

    public IReadOnlyCollection<TEntry> Entries => new ReadOnlyCollection<TEntry>(_entries.Values.OrderBy(entry => entry.Id).ToArray());

    public bool Contains(StableId id) => _entries.ContainsKey(id);

    public bool TryGet(StableId id, out TEntry? entry) => _entries.TryGetValue(id, out entry);

    public TEntry GetRequired(StableId id)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            return entry;
        }

        throw new KeyNotFoundException($"Unknown registry ID '{id}'.");
    }
}
