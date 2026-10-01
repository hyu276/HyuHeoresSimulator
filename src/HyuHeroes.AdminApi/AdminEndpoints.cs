/**
 * ADMIN_ENDPOINTS
 * Purpose: Exposes the content revision lifecycle over authenticated HTTP without duplicating workflow rules.
 * Connections: Delegates all mutations to ContentWorkflowService and all reads to IContentRevisionRepository; policies come from AdminAuthorization.
 * Risk: High because endpoint-to-policy mapping and concurrency-token propagation control who may alter or publish authoritative content.
 */
using System.Security.Claims;
using System.Text.Json;
using HyuHeroes.ContentStore.Postgres;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Content.Workflow;
using HyuHeroes.Gameplay.Core;

namespace HyuHeroes.AdminApi;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/content")
            .RequireAuthorization(AdminAuthorization.ReaderPolicy);

        group.MapGet("/workspaces/{workspaceId}/revisions", ListRevisions);
        group.MapGet("/workspaces/{workspaceId}/revisions/{revision:int}", GetRevision);
        group.MapGet("/workspaces/{workspaceId}/revisions/{revision:int}/audit", GetAudit);
        group.MapGet("/publications/{jobId:guid}", GetPublicationJob);

        group.MapPost("/workspaces/{workspaceId}/revisions", CreateDraft)
            .RequireAuthorization(AdminAuthorization.AuthorPolicy);
        group.MapPut("/workspaces/{workspaceId}/revisions/{revision:int}", UpdateDraft)
            .RequireAuthorization(AdminAuthorization.AuthorPolicy);
        group.MapPost("/workspaces/{workspaceId}/revisions/{revision:int}/submit-review", SubmitReview)
            .RequireAuthorization(AdminAuthorization.AuthorPolicy);

        group.MapPost("/workspaces/{workspaceId}/revisions/{revision:int}/return-draft", ReturnDraft)
            .RequireAuthorization(AdminAuthorization.ReviewerPolicy);
        group.MapPost("/workspaces/{workspaceId}/revisions/{revision:int}/approve", Approve)
            .RequireAuthorization(AdminAuthorization.ReviewerPolicy);

        group.MapPost("/workspaces/{workspaceId}/revisions/{revision:int}/publish", Publish)
            .RequireAuthorization(AdminAuthorization.PublisherPolicy);

        return endpoints;
    }

    private static IResult ListRevisions(
        string workspaceId,
        IContentRevisionRepository repository)
    {
        if (!StableId.TryParse(workspaceId, out var id))
        {
            return InvalidWorkspaceId();
        }

        return Results.Ok(repository.List(id).Select(AdminApiContractMapper.ToSummary));
    }

    private static IResult GetRevision(
        string workspaceId,
        int revision,
        IContentRevisionRepository repository)
    {
        if (!TryKey(workspaceId, revision, out var key))
        {
            return InvalidRevisionKey();
        }

        var value = repository.Get(key);
        return value is null
            ? Results.NotFound()
            : Results.Ok(AdminApiContractMapper.ToDetail(value));
    }

    private static IResult GetAudit(
        string workspaceId,
        int revision,
        IContentRevisionRepository repository)
    {
        if (!TryKey(workspaceId, revision, out var key))
        {
            return InvalidRevisionKey();
        }

        if (repository.Get(key) is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(repository.GetAudit(key).Select(AdminApiContractMapper.ToAudit));
    }

    private static IResult GetPublicationJob(
        Guid jobId,
        PostgresPublicationOutboxRepository outbox)
    {
        var job = outbox.Get(jobId);
        return job is null
            ? Results.NotFound()
            : Results.Ok(AdminApiContractMapper.ToPublicationJob(job));
    }

    private static IResult CreateDraft(
        string workspaceId,
        CreateDraftRequest request,
        ContentWorkflowService service,
        HttpContext context) =>
        Execute(() =>
        {
            if (!StableId.TryParse(workspaceId, out var id))
            {
                throw new ArgumentException("Workspace ID must be a namespaced StableId.");
            }

            var content = ParseAuthoringContent(request.Content);
            return Results.Created(
                $"/api/content/workspaces/{workspaceId}/revisions",
                AdminApiContractMapper.ToDetail(
                    service.CreateDraft(
                        id,
                        content,
                        Actor(context.User),
                        request.Reason,
                        DateTimeOffset.UtcNow)));
        });

    private static IResult UpdateDraft(
        string workspaceId,
        int revision,
        UpdateDraftRequest request,
        ContentWorkflowService service,
        HttpContext context) =>
        Execute(() =>
        {
            var key = RequiredKey(workspaceId, revision);
            var content = ParseAuthoringContent(request.Content);
            return Results.Ok(
                AdminApiContractMapper.ToDetail(
                    service.UpdateDraft(
                        key,
                        content,
                        Actor(context.User),
                        request.Reason,
                        request.StoreVersion,
                        DateTimeOffset.UtcNow)));
        });

    private static IResult SubmitReview(
        string workspaceId,
        int revision,
        TransitionRequest request,
        ContentWorkflowService service,
        HttpContext context) =>
        Transition(
            workspaceId,
            revision,
            request,
            context.User,
            (key, actor, occurredAt) =>
                service.SubmitForReview(
                    key,
                    actor,
                    request.Reason,
                    request.StoreVersion,
                    occurredAt));

    private static IResult ReturnDraft(
        string workspaceId,
        int revision,
        TransitionRequest request,
        ContentWorkflowService service,
        HttpContext context) =>
        Transition(
            workspaceId,
            revision,
            request,
            context.User,
            (key, actor, occurredAt) =>
                service.ReturnToDraft(
                    key,
                    actor,
                    request.Reason,
                    request.StoreVersion,
                    occurredAt));

    private static IResult Approve(
        string workspaceId,
        int revision,
        TransitionRequest request,
        ContentWorkflowService service,
        HttpContext context) =>
        Transition(
            workspaceId,
            revision,
            request,
            context.User,
            (key, actor, occurredAt) =>
                service.Approve(
                    key,
                    actor,
                    request.Reason,
                    request.StoreVersion,
                    occurredAt));

    private static IResult Publish(
        string workspaceId,
        int revision,
        PublishRevisionRequest request,
        ContentWorkflowService service,
        PostgresPublicationOutboxRepository outbox,
        HttpContext context) =>
        Execute(() =>
        {
            var key = RequiredKey(workspaceId, revision);
            var occurredAt = DateTimeOffset.UtcNow;
            var actor = Actor(context.User);
            var prepared = service.PreparePublication(
                key,
                new ContentPublishRequest(
                    request.ContentVersion,
                    request.PackagePath,
                    existingManifest: null,
                    request.MakeDefault),
                request.StoreVersion,
                occurredAt);
            var job = outbox.Enqueue(
                prepared,
                actor,
                request.Reason,
                occurredAt);
            return Results.Accepted(
                $"/api/content/publications/{job.JobId}",
                AdminApiContractMapper.ToPublicationJob(job));
        });

    private static IResult Transition(
        string workspaceId,
        int revision,
        TransitionRequest request,
        ClaimsPrincipal principal,
        Func<ContentRevisionKey, string, DateTimeOffset, ContentWorkspaceRevision> action) =>
        Execute(() =>
        {
            var result = action(
                RequiredKey(workspaceId, revision),
                Actor(principal),
                DateTimeOffset.UtcNow);
            return Results.Ok(AdminApiContractMapper.ToDetail(result));
        });

    private static GameplayContentPackage ParseAuthoringContent(JsonElement content) =>
        GameplayContentJsonLoader.ParseAuthoringSnapshot(content.GetRawText());

    private static ContentRevisionKey RequiredKey(string workspaceId, int revision)
    {
        if (!TryKey(workspaceId, revision, out var key))
        {
            throw new ArgumentException("Workspace ID and revision must form a valid revision key.");
        }

        return key;
    }

    private static bool TryKey(
        string workspaceId,
        int revision,
        out ContentRevisionKey key)
    {
        if (revision > 0 && StableId.TryParse(workspaceId, out var id))
        {
            key = new ContentRevisionKey(id, revision);
            return true;
        }

        key = null!;
        return false;
    }

    private static string Actor(ClaimsPrincipal principal) =>
        AdminAuthorization.RequireActor(principal);

    private static IResult Execute(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ContentConcurrencyException error)
        {
            return Results.Conflict(new { code = "content_concurrency", error = error.Message });
        }
        catch (InvalidDataException error)
        {
            return Results.BadRequest(new { code = "invalid_content", error = error.Message });
        }
        catch (JsonException error)
        {
            return Results.BadRequest(new { code = "invalid_json", error = error.Message });
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { code = "invalid_request", error = error.Message });
        }
        catch (InvalidOperationException error)
        {
            return Results.Conflict(new { code = "invalid_transition", error = error.Message });
        }
    }

    private static IResult InvalidWorkspaceId() =>
        Results.BadRequest(new { code = "invalid_workspace_id" });

    private static IResult InvalidRevisionKey() =>
        Results.BadRequest(new { code = "invalid_revision_key" });
}
