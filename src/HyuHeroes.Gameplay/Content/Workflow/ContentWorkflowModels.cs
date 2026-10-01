/**
 * CONTENT_WORKFLOW_MODELS
 * Purpose: Defines immutable authoring-release revisions, workflow states, publication metadata, and audit entries independently from match runtime state.
 * Connections: ContentWorkflowService transitions these records; IContentRevisionRepository persists them; ContentPublisher materializes approved revisions into immutable packages.
 * Risk: High because workflow state and optimistic versioning determine which authored content is eligible for publication.
 */
using System;
using HyuHeroes.Gameplay.Core;

namespace HyuHeroes.Gameplay.Content.Workflow;

public enum ContentWorkflowState
{
    Draft,
    Review,
    Approved,
    Published
}

public sealed class ContentRevisionKey : IEquatable<ContentRevisionKey>
{
    public ContentRevisionKey(StableId workspaceId, int revision)
    {
        WorkspaceId = workspaceId == default
            ? throw new ArgumentException("Workspace ID must be a non-default StableId.", nameof(workspaceId))
            : workspaceId;
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Revision must be positive.");
        }

        Revision = revision;
    }

    public StableId WorkspaceId { get; }
    public int Revision { get; }

    public bool Equals(ContentRevisionKey? other) =>
        other is not null &&
        WorkspaceId == other.WorkspaceId &&
        Revision == other.Revision;

    public override bool Equals(object? obj) => Equals(obj as ContentRevisionKey);

    public override int GetHashCode() => HashCode.Combine(WorkspaceId, Revision);

    public override string ToString() => $"{WorkspaceId.Value}@{Revision}";
}

public sealed class ContentPublicationRecord
{
    public ContentPublicationRecord(
        string contentVersion,
        string contentHash,
        string packagePath,
        DateTimeOffset publishedAt)
    {
        ContentVersion = RequireText(contentVersion, nameof(contentVersion));
        ContentHash = RequireText(contentHash, nameof(contentHash));
        PackagePath = RequireText(packagePath, nameof(packagePath));
        PublishedAt = publishedAt.ToUniversalTime();
    }

    public string ContentVersion { get; }
    public string ContentHash { get; }
    public string PackagePath { get; }
    public DateTimeOffset PublishedAt { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
}

public sealed class ContentWorkspaceRevision
{
    public ContentWorkspaceRevision(
        ContentRevisionKey key,
        ContentWorkflowState state,
        GameplayContentPackage content,
        DateTimeOffset createdAt,
        string createdBy,
        DateTimeOffset updatedAt,
        string updatedBy,
        long storeVersion = 0,
        ContentPublicationRecord? publication = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        if (!Enum.IsDefined(typeof(ContentWorkflowState), state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (storeVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(storeVersion), "Store version cannot be negative.");
        }

        State = state;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        CreatedAt = createdAt.ToUniversalTime();
        CreatedBy = RequireActor(createdBy, nameof(createdBy));
        UpdatedAt = updatedAt.ToUniversalTime();
        UpdatedBy = RequireActor(updatedBy, nameof(updatedBy));
        StoreVersion = storeVersion;
        Publication = publication;

        if (State == ContentWorkflowState.Published && Publication is null)
        {
            throw new ArgumentException("Published revisions require publication metadata.", nameof(publication));
        }

        if (State != ContentWorkflowState.Published && Publication is not null)
        {
            throw new ArgumentException("Only Published revisions may contain publication metadata.", nameof(publication));
        }
    }

    public ContentRevisionKey Key { get; }
    public ContentWorkflowState State { get; }
    public GameplayContentPackage Content { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CreatedBy { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string UpdatedBy { get; }
    public long StoreVersion { get; }
    public ContentPublicationRecord? Publication { get; }

    public ContentWorkspaceRevision With(
        ContentWorkflowState? state = null,
        GameplayContentPackage? content = null,
        DateTimeOffset? updatedAt = null,
        string? updatedBy = null,
        long? storeVersion = null,
        ContentPublicationRecord? publication = null,
        bool clearPublication = false) =>
        new(
            Key,
            state ?? State,
            content ?? Content,
            CreatedAt,
            CreatedBy,
            updatedAt ?? UpdatedAt,
            updatedBy ?? UpdatedBy,
            storeVersion ?? StoreVersion,
            clearPublication ? null : publication ?? Publication);

    private static string RequireActor(string actor, string parameterName) =>
        string.IsNullOrWhiteSpace(actor)
            ? throw new ArgumentException("Actor cannot be empty.", parameterName)
            : actor.Trim();
}

public sealed class ContentWorkflowAuditEntry
{
    public ContentWorkflowAuditEntry(
        long sequence,
        ContentRevisionKey key,
        string action,
        ContentWorkflowState? fromState,
        ContentWorkflowState toState,
        string actor,
        DateTimeOffset occurredAt,
        string reason)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Audit sequence cannot be negative.");
        }

        Key = key ?? throw new ArgumentNullException(nameof(key));
        Action = string.IsNullOrWhiteSpace(action)
            ? throw new ArgumentException("Audit action cannot be empty.", nameof(action))
            : action.Trim();
        Actor = string.IsNullOrWhiteSpace(actor)
            ? throw new ArgumentException("Audit actor cannot be empty.", nameof(actor))
            : actor.Trim();
        Reason = string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("Audit reason cannot be empty.", nameof(reason))
            : reason.Trim();

        if (fromState is { } previous && !Enum.IsDefined(typeof(ContentWorkflowState), previous))
        {
            throw new ArgumentOutOfRangeException(nameof(fromState));
        }

        if (!Enum.IsDefined(typeof(ContentWorkflowState), toState))
        {
            throw new ArgumentOutOfRangeException(nameof(toState));
        }

        Sequence = sequence;
        FromState = fromState;
        ToState = toState;
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public long Sequence { get; }
    public ContentRevisionKey Key { get; }
    public string Action { get; }
    public ContentWorkflowState? FromState { get; }
    public ContentWorkflowState ToState { get; }
    public string Actor { get; }
    public DateTimeOffset OccurredAt { get; }
    public string Reason { get; }

    public ContentWorkflowAuditEntry WithSequence(long sequence) =>
        new(sequence, Key, Action, FromState, ToState, Actor, OccurredAt, Reason);
}

public sealed class ContentPublishRequest
{
    public ContentPublishRequest(
        string contentVersion,
        string packagePath,
        ContentPackageManifest? existingManifest = null,
        bool makeDefault = true)
    {
        ContentVersion = string.IsNullOrWhiteSpace(contentVersion)
            ? throw new ArgumentException("Content version cannot be empty.", nameof(contentVersion))
            : contentVersion.Trim();
        PackagePath = string.IsNullOrWhiteSpace(packagePath)
            ? throw new ArgumentException("Package path cannot be empty.", nameof(packagePath))
            : packagePath.Trim();
        ExistingManifest = existingManifest;
        MakeDefault = makeDefault;
    }

    public string ContentVersion { get; }
    public string PackagePath { get; }
    public ContentPackageManifest? ExistingManifest { get; }
    public bool MakeDefault { get; }
}

public sealed class PreparedContentPublication
{
    public PreparedContentPublication(
        ContentRevisionKey key,
        long expectedStoreVersion,
        ContentPublishRequest request,
        DateTimeOffset publishedAt,
        PublishedContentArtifact artifact)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        if (expectedStoreVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedStoreVersion),
                "Expected store version must be positive.");
        }

        ExpectedStoreVersion = expectedStoreVersion;
        Request = request ?? throw new ArgumentNullException(nameof(request));
        PublishedAt = publishedAt.ToUniversalTime();
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
    }

    public ContentRevisionKey Key { get; }
    public long ExpectedStoreVersion { get; }
    public ContentPublishRequest Request { get; }
    public DateTimeOffset PublishedAt { get; }
    public PublishedContentArtifact Artifact { get; }
}

public sealed class ContentPublishResult
{
    public ContentPublishResult(
        ContentWorkspaceRevision revision,
        PublishedContentArtifact artifact)
    {
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
    }

    public ContentWorkspaceRevision Revision { get; }
    public PublishedContentArtifact Artifact { get; }
}
