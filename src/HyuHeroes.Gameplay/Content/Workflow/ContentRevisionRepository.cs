/**
 * CONTENT_REVISION_REPOSITORY
 * Purpose: Defines the persistence boundary for content workflow revisions and provides a deterministic in-memory adapter for tests/prototypes.
 * Connections: ContentWorkflowService depends only on this contract; durable adapters can replace InMemoryContentRevisionRepository without changing workflow rules.
 * Risk: High because optimistic concurrency and audit append semantics protect authoring history from lost updates.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HyuHeroes.Gameplay.Core;

namespace HyuHeroes.Gameplay.Content.Workflow;

public interface IContentRevisionRepository
{
    ContentWorkspaceRevision? Get(ContentRevisionKey key);

    ContentWorkspaceRevision? GetLatest(StableId workspaceId);

    IReadOnlyList<ContentWorkspaceRevision> List(StableId workspaceId);

    IReadOnlyList<ContentWorkflowAuditEntry> GetAudit(ContentRevisionKey key);

    ContentWorkspaceRevision Save(
        ContentWorkspaceRevision revision,
        long? expectedStoreVersion,
        ContentWorkflowAuditEntry auditEntry);
}

public sealed class ContentConcurrencyException : InvalidOperationException
{
    public ContentConcurrencyException(string message) : base(message)
    {
    }
}

public sealed class InMemoryContentRevisionRepository : IContentRevisionRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ContentWorkspaceRevision> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ContentWorkflowAuditEntry>> _audit = new(StringComparer.Ordinal);

    public ContentWorkspaceRevision? Get(ContentRevisionKey key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        lock (_gate)
        {
            return _revisions.TryGetValue(StorageKey(key), out var value) ? value : null;
        }
    }

    public ContentWorkspaceRevision? GetLatest(StableId workspaceId)
    {
        if (workspaceId == default) throw new ArgumentException("Workspace ID must be non-default.", nameof(workspaceId));
        lock (_gate)
        {
            return _revisions.Values
                .Where(value => value.Key.WorkspaceId == workspaceId)
                .OrderByDescending(value => value.Key.Revision)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<ContentWorkspaceRevision> List(StableId workspaceId)
    {
        if (workspaceId == default) throw new ArgumentException("Workspace ID must be non-default.", nameof(workspaceId));
        lock (_gate)
        {
            return new ReadOnlyCollection<ContentWorkspaceRevision>(
                _revisions.Values
                    .Where(value => value.Key.WorkspaceId == workspaceId)
                    .OrderBy(value => value.Key.Revision)
                    .ToArray());
        }
    }

    public IReadOnlyList<ContentWorkflowAuditEntry> GetAudit(ContentRevisionKey key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        lock (_gate)
        {
            return _audit.TryGetValue(StorageKey(key), out var entries)
                ? new ReadOnlyCollection<ContentWorkflowAuditEntry>(entries.ToArray())
                : Array.Empty<ContentWorkflowAuditEntry>();
        }
    }

    public ContentWorkspaceRevision Save(
        ContentWorkspaceRevision revision,
        long? expectedStoreVersion,
        ContentWorkflowAuditEntry auditEntry)
    {
        if (revision is null) throw new ArgumentNullException(nameof(revision));
        if (auditEntry is null) throw new ArgumentNullException(nameof(auditEntry));
        if (!revision.Key.Equals(auditEntry.Key))
        {
            throw new ArgumentException("Revision and audit entry must use the same key.", nameof(auditEntry));
        }

        lock (_gate)
        {
            var key = StorageKey(revision.Key);
            var exists = _revisions.TryGetValue(key, out var current);
            ValidateConcurrency(revision.Key, exists, current, expectedStoreVersion);

            var nextStoreVersion = exists
                ? checked(current!.StoreVersion + 1)
                : 1;
            var saved = revision.With(storeVersion: nextStoreVersion);
            _revisions[key] = saved;

            if (!_audit.TryGetValue(key, out var entries))
            {
                entries = new List<ContentWorkflowAuditEntry>();
                _audit.Add(key, entries);
            }

            entries.Add(auditEntry.WithSequence(entries.Count + 1L));
            return saved;
        }
    }

    private static void ValidateConcurrency(
        ContentRevisionKey key,
        bool exists,
        ContentWorkspaceRevision? current,
        long? expectedStoreVersion)
    {
        if (!exists)
        {
            if (expectedStoreVersion is not null)
            {
                throw new ContentConcurrencyException(
                    $"Revision '{key}' does not exist but expected store version '{expectedStoreVersion}'.");
            }

            return;
        }

        if (current is null)
        {
            throw new InvalidOperationException(
                $"Revision '{key}' was reported as existing without a stored value.");
        }

        if (expectedStoreVersion is null || current.StoreVersion != expectedStoreVersion.Value)
        {
            throw new ContentConcurrencyException(
                $"Revision '{key}' changed concurrently. Expected '{expectedStoreVersion?.ToString() ?? "<new>"}', actual '{current.StoreVersion}'.");
        }
    }

    private static string StorageKey(ContentRevisionKey key) =>
        $"{key.WorkspaceId.Value}:{key.Revision}";
}
