/**
 * ADMIN_API_INTEGRATION_TESTS
 * Purpose: Verifies JWT authentication, role boundaries, HTTP workflow transitions, optimistic concurrency, audit identity, and PostgreSQL persistence end to end.
 * Connections: Boots Program through WebApplicationFactory against HYU_TEST_POSTGRES and exercises the real ContentWorkflowService/PostgresContentRevisionRepository stack.
 * Risk: High because a passing unit-only workflow is insufficient if HTTP authorization or concurrency tokens are mapped incorrectly.
 */
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using HyuHeroes.AdminApi;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Validation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace HyuHeroes.AdminApi.Tests;

public sealed class AdminApiIntegrationTests : IDisposable
{
    private const string Issuer = "hyu-admin-tests";
    private const string Audience = "hyu-admin-api";
    private const string SigningKey = "hyu-admin-api-test-signing-key-32-bytes-minimum";
    private readonly WebApplicationFactory<Program> _factory;

    public AdminApiIntegrationTests()
    {
        var postgres = Environment.GetEnvironmentVariable("HYU_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(postgres))
        {
            throw new InvalidOperationException("HYU_TEST_POSTGRES is required for Admin API integration tests.");
        }

        Environment.SetEnvironmentVariable("HYU_CONTENT_POSTGRES", postgres);
        Environment.SetEnvironmentVariable("HYU_ADMIN_JWT_ISSUER", Issuer);
        Environment.SetEnvironmentVariable("HYU_ADMIN_JWT_AUDIENCE", Audience);
        Environment.SetEnvironmentVariable("HYU_ADMIN_JWT_SIGNING_KEY", SigningKey);
        _factory = new WebApplicationFactory<Program>();
    }

    [Fact]
    public async Task AuthenticationAndRolePolicies_BlockAnonymousAndWrongRole()
    {
        using var client = _factory.CreateClient();
        var workspaceId = WorkspaceId();

        var anonymous = await client.GetAsync($"/api/content/workspaces/{workspaceId}/revisions");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Authorize(client, "author.alice", "Author");
        var draft = await CreateDraft(client, workspaceId, 3m);

        var forbidden = await client.PostAsJsonAsync(
            $"/api/content/workspaces/{workspaceId}/revisions/{draft.Revision.Revision}/approve",
            new TransitionRequest("Attempt unauthorized approval", draft.Revision.StoreVersion));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task FullLifecycle_UsesRoleBoundariesAndJwtSubjectAuditActors()
    {
        using var client = _factory.CreateClient();
        var workspaceId = WorkspaceId();

        Authorize(client, "author.alice", "Author");
        var draft = await CreateDraft(client, workspaceId, 3m);

        var reviewResponse = await client.PostAsJsonAsync(
            RevisionAction(workspaceId, draft, "submit-review"),
            new TransitionRequest("Ready for review", draft.Revision.StoreVersion));
        var review = await ReadSuccess<RevisionDetailResponse>(reviewResponse);
        Assert.Equal("Review", review.Revision.State);

        Authorize(client, "reviewer.bob", "Reviewer");
        var approveResponse = await client.PostAsJsonAsync(
            RevisionAction(workspaceId, review, "approve"),
            new TransitionRequest("Approved by reviewer", review.Revision.StoreVersion));
        var approved = await ReadSuccess<RevisionDetailResponse>(approveResponse);
        Assert.Equal("Approved", approved.Revision.State);

        Authorize(client, "publisher.carol", "Publisher");
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var publishResponse = await client.PostAsJsonAsync(
            RevisionAction(workspaceId, approved, "publish"),
            new PublishRevisionRequest(
                "Publish approved revision",
                approved.Revision.StoreVersion,
                $"prototype.api.{suffix}",
                $"content/packages/api-{suffix}.json"));
        var published = await ReadSuccess<PublishRevisionResponse>(publishResponse);

        Assert.Equal("Published", published.Revision.Revision.State);
        Assert.NotNull(published.Revision.Revision.Publication);
        Assert.Contains("sha256:", published.PackageJson, StringComparison.Ordinal);
        Assert.Contains($"prototype.api.{suffix}", published.ManifestJson, StringComparison.Ordinal);

        Authorize(client, "author.alice", "Author");
        var auditResponse = await client.GetAsync(
            $"/api/content/workspaces/{workspaceId}/revisions/{draft.Revision.Revision}/audit");
        var audit = await ReadSuccess<AuditEntryResponse[]>(auditResponse);

        Assert.Equal(
            new[] { "CREATE_DRAFT", "SUBMIT_REVIEW", "APPROVE", "PUBLISH" },
            audit.Select(entry => entry.Action).ToArray());
        Assert.Equal(
            new[] { "author.alice", "author.alice", "reviewer.bob", "publisher.carol" },
            audit.Select(entry => entry.Actor).ToArray());
    }

    [Fact]
    public async Task StaleStoreVersion_ReturnsConflictWithoutOverwritingDraft()
    {
        using var client = _factory.CreateClient();
        var workspaceId = WorkspaceId();
        Authorize(client, "author.concurrent", "Author");

        var draft = await CreateDraft(client, workspaceId, 3m);
        var firstUpdate = await client.PutAsJsonAsync(
            RevisionPath(workspaceId, draft.Revision.Revision),
            new UpdateDraftRequest(
                PackageElement(4m),
                "First editor update",
                draft.Revision.StoreVersion));
        var updated = await ReadSuccess<RevisionDetailResponse>(firstUpdate);
        Assert.Equal(draft.Revision.StoreVersion + 1, updated.Revision.StoreVersion);

        var staleUpdate = await client.PutAsJsonAsync(
            RevisionPath(workspaceId, draft.Revision.Revision),
            new UpdateDraftRequest(
                PackageElement(8m),
                "Stale editor update",
                draft.Revision.StoreVersion));
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);

        var conflict = await staleUpdate.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("content_concurrency", conflict?["code"]);

        var current = await client.GetFromJsonAsync<RevisionDetailResponse>(
            RevisionPath(workspaceId, draft.Revision.Revision));
        Assert.NotNull(current);
        Assert.Equal(updated.Revision.StoreVersion, current.Revision.StoreVersion);
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<RevisionDetailResponse> CreateDraft(
        HttpClient client,
        string workspaceId,
        decimal cost)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/content/workspaces/{workspaceId}/revisions",
            new CreateDraftRequest(PackageElement(cost), "Create API draft"));
        return await ReadSuccess<RevisionDetailResponse>(response);
    }

    private static string RevisionPath(string workspaceId, int revision) =>
        $"/api/content/workspaces/{workspaceId}/revisions/{revision}";

    private static string RevisionAction(
        string workspaceId,
        RevisionDetailResponse revision,
        string action) =>
        $"{RevisionPath(workspaceId, revision.Revision.Revision)}/{action}";

    private static async Task<T> ReadSuccess<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var value = await response.Content.ReadFromJsonAsync<T>();
        return value ?? throw new InvalidDataException("HTTP response body was empty.");
    }

    private static void Authorize(HttpClient client, string subject, params string[] roles)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, subject) };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10),
            credentials);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
    }

    private static string WorkspaceId() =>
        "workspace.api_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static System.Text.Json.JsonElement PackageElement(decimal cost)
    {
        using var document = System.Text.Json.JsonDocument.Parse(CreatePackageJson(cost));
        return document.RootElement.Clone();
    }

    private static string CreatePackageJson(decimal cost)
    {
        var catalog = GameplayRegistryCatalog.CreateSchemaV1();
        var card = new CardDefinition(
            Header(StableId.Parse("card.api_guardian")),
            CardType.Unit,
            new Classification(
                StableId.Parse("alignment.neutral"),
                StableId.Parse("class.control")),
            new StatBlock(new[]
            {
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.cost"), cost),
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.attack"), 2m),
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.max_health"), 5m)
            }));

        var presentation = new GameplayPresentationSnapshot(
            "en-US",
            new[]
            {
                new LocalizationBundle(
                    "en-US",
                    new[]
                    {
                        new LocalizedDefinitionText(
                            card.Header.LocalizationKey,
                            "API Guardian",
                            "Admin API integration fixture.")
                    })
            });

        var package = new GameplayContentPackage(
            GameplaySchemaValidator.SupportedSchemaVersion,
            "authoring.api",
            DateTimeOffset.Parse("2026-10-01T15:00:00Z", CultureInfo.InvariantCulture),
            GameplayRegistrySnapshot.FromCatalog(catalog),
            Array.Empty<AbilityDefinition>(),
            new[] { card },
            presentation: presentation);
        return GameplayContentCanonicalWriter.Serialize(package, indented: false);
    }

    private static GameplayDefinitionHeader Header(StableId id) =>
        new(
            id,
            GameplaySchemaValidator.SupportedSchemaVersion,
            1,
            ContentStatus.Published,
            $"loc.{id.Value.Replace('.', '_')}");
}
