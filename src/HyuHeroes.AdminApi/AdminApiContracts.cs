/**
 * ADMIN_API_CONTRACTS
 * Purpose: Defines HTTP request/response DTOs and deterministic mappings for content workflow revisions.
 * Connections: AdminEndpoints consumes these contracts while ContentWorkflowService and IContentRevisionRepository remain the domain/application boundary.
 * Risk: Medium because DTO drift can expose incorrect concurrency tokens or publication identities to the Admin Dashboard.
 */
using System.Text.Json;
using HyuHeroes.ContentStore.Postgres;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Content.Workflow;

namespace HyuHeroes.AdminApi;

public sealed record CreateDraftRequest(JsonElement Content, string Reason);

public sealed record UpdateDraftRequest(
    JsonElement Content,
    string Reason,
    long StoreVersion);

public sealed record TransitionRequest(string Reason, long StoreVersion);

public sealed record PublishRevisionRequest(
    string Reason,
    long StoreVersion,
    string ContentVersion,
    string PackagePath,
    bool MakeDefault = true);

public sealed record PublicationResponse(
    string ContentVersion,
    string ContentHash,
    string PackagePath,
    DateTimeOffset PublishedAt);

public sealed record RevisionSummaryResponse(
    string WorkspaceId,
    int Revision,
    string State,
    long StoreVersion,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    PublicationResponse? Publication);

public sealed record RevisionDetailResponse(
    RevisionSummaryResponse Revision,
    JsonElement Content);

public sealed record AuditEntryResponse(
    long Sequence,
    string Action,
    string? FromState,
    string ToState,
    string Actor,
    DateTimeOffset OccurredAt,
    string Reason);

public sealed record PublicationJobResponse(
    Guid JobId,
    string WorkspaceId,
    int Revision,
    string Stage,
    int AttemptCount,
    string ContentVersion,
    string ContentHash,
    string PackagePath,
    DateTimeOffset PublishedAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class AdminApiContractMapper
{
    public static RevisionSummaryResponse ToSummary(ContentWorkspaceRevision revision) =>
        new(
            revision.Key.WorkspaceId.Value,
            revision.Key.Revision,
            revision.State.ToString(),
            revision.StoreVersion,
            revision.CreatedAt,
            revision.CreatedBy,
            revision.UpdatedAt,
            revision.UpdatedBy,
            revision.Publication is null
                ? null
                : new PublicationResponse(
                    revision.Publication.ContentVersion,
                    revision.Publication.ContentHash,
                    revision.Publication.PackagePath,
                    revision.Publication.PublishedAt));

    public static RevisionDetailResponse ToDetail(ContentWorkspaceRevision revision) =>
        new(
            ToSummary(revision),
            ParseJson(GameplayContentCanonicalWriter.Serialize(revision.Content)));

    public static AuditEntryResponse ToAudit(ContentWorkflowAuditEntry entry) =>
        new(
            entry.Sequence,
            entry.Action,
            entry.FromState?.ToString(),
            entry.ToState.ToString(),
            entry.Actor,
            entry.OccurredAt,
            entry.Reason);

    public static PublicationJobResponse ToPublicationJob(PublicationOutboxJob job) =>
        new(
            job.JobId,
            job.Key.WorkspaceId.Value,
            job.Key.Revision,
            job.Stage.ToString(),
            job.AttemptCount,
            job.ContentVersion,
            job.ContentHash,
            job.PackagePath,
            job.PublishedAt,
            job.LastError,
            job.CreatedAt,
            job.UpdatedAt);

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
