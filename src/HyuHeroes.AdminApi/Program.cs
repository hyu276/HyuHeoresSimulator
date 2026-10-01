/**
 * ADMIN_API_HOST
 * Purpose: Boots the authenticated content-management HTTP API and wires PostgreSQL persistence to the existing workflow service.
 * Connections: Uses PostgresContentRevisionRepository, ContentWorkflowService, AdminAuthorization, and AdminEndpoints.
 * Risk: High because startup configuration selects the authoritative authoring store and security boundary.
 */
using HyuHeroes.AdminApi;
using HyuHeroes.ContentStore.Postgres;
using HyuHeroes.Gameplay.Content.Workflow;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["HYU_CONTENT_POSTGRES"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Required configuration 'HYU_CONTENT_POSTGRES' is missing.");
}

var artifactRoot = builder.Configuration["HYU_CONTENT_ARTIFACT_ROOT"];
if (string.IsNullOrWhiteSpace(artifactRoot))
{
    throw new InvalidOperationException("Required configuration 'HYU_CONTENT_ARTIFACT_ROOT' is missing.");
}

var repository = new PostgresContentRevisionRepository(connectionString);
repository.EnsureSchema();
var outbox = new PostgresPublicationOutboxRepository(connectionString);
outbox.EnsureSchema();
var artifactStore = new FileSystemContentArtifactStore(artifactRoot);

builder.Services.AddSingleton(repository);
builder.Services.AddSingleton<IContentRevisionRepository>(repository);
builder.Services.AddSingleton(outbox);
builder.Services.AddSingleton<IContentArtifactStore>(artifactStore);
builder.Services.AddSingleton<ContentWorkflowService>();
builder.Services.AddHostedService<PublicationWorker>();
builder.Services.AddAdminAuthorization(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();
app.MapAdminContentEndpoints();

app.Run();

public partial class Program;
