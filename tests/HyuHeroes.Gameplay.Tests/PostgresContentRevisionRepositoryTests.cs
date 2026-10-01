/**
 * POSTGRES_CONTENT_REVISION_REPOSITORY_TESTS
 * Purpose: Protects authoring snapshot persistence contracts and exercises the PostgreSQL adapter end-to-end when CI provides a test database.
 * Connections: Exercises GameplayContentJsonLoader.LoadAuthoringSnapshot, PostgresContentRevisionRepository, ContentWorkflowService, and ContentPublisher.
 * Risk: High because persistence regressions can lose revision history, break optimistic concurrency, or separate audit rows from content mutations.
 */
using System.Globalization;
using HyuHeroes.ContentStore.Postgres;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Content.Workflow;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class PostgresContentRevisionRepositoryTests
{
    private static readonly DateTimeOffset T0 =
        DateTimeOffset.Parse("2026-10-01T16:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void AuthoringSnapshot_RoundTripsUnsignedDraftWithoutPublicationValidation()
    {
        var package = CreatePackage(
            ContentStatus.Draft,
            missingAbilityReference: true);
        var json = GameplayContentCanonicalWriter.Serialize(package, indented: false);

        var loaded = GameplayContentJsonLoader.LoadAuthoringSnapshot(json);

        Assert.Null(loaded.ContentHash);
        Assert.Equal(ContentStatus.Draft, Assert.Single(loaded.Cards).Header.Status);
        Assert.Equal(
            StableId.Parse("ability.missing"),
            Assert.Single(Assert.Single(loaded.Cards).Abilities).ReferencedAbilityId);
    }

    [Fact]
    public void Schema_ContainsRevisionConcurrencyAndAuditConstraints()
    {
        Assert.Contains("PRIMARY KEY (workspace_id, revision)", PostgresContentRevisionSchema.Sql, StringComparison.Ordinal);
        Assert.Contains("store_version bigint NOT NULL", PostgresContentRevisionSchema.Sql, StringComparison.Ordinal);
        Assert.Contains("content_revision_audit", PostgresContentRevisionSchema.Sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (workspace_id, revision)", PostgresContentRevisionSchema.Sql, StringComparison.Ordinal);
        Assert.Contains("ON DELETE RESTRICT", PostgresContentRevisionSchema.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_ExecutesWorkflowAgainstPostgres_WhenCiDatabaseIsAvailable()
    {
        var connectionString = Environment.GetEnvironmentVariable("HYU_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var repository = new PostgresContentRevisionRepository(connectionString);
        repository.EnsureSchema();
        var service = new ContentWorkflowService(repository);
        var workspaceId = StableId.Parse(
            "workspace.pgtest_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        var draft = service.CreateDraft(
            workspaceId,
            CreatePackage(ContentStatus.Published),
            "author.ci",
            "Create PostgreSQL integration draft",
            T0);
        var review = service.SubmitForReview(
            draft.Key,
            "author.ci",
            "Submit PostgreSQL integration draft",
            draft.StoreVersion,
            T0.AddMinutes(1));
        var approved = service.Approve(
            review.Key,
            "reviewer.ci",
            "Approve PostgreSQL integration draft",
            review.StoreVersion,
            T0.AddMinutes(2));
        var published = service.Publish(
            approved.Key,
            new ContentPublishRequest(
                "prototype.pg." + workspaceId.Value.Split('_').Last(),
                "content/packages/pg-integration.json"),
            "publisher.ci",
            "Publish PostgreSQL integration draft",
            approved.StoreVersion,
            T0.AddMinutes(3));

        var loaded = repository.Get(published.Revision.Key);
        Assert.NotNull(loaded);
        Assert.Equal(ContentWorkflowState.Published, loaded.State);
        Assert.Equal(published.Revision.StoreVersion, loaded.StoreVersion);
        Assert.Equal(published.Artifact.Package.ContentHash, loaded.Publication?.ContentHash);

        var audit = repository.GetAudit(published.Revision.Key);
        Assert.Equal(
            new[] { "CREATE_DRAFT", "SUBMIT_REVIEW", "APPROVE", "PUBLISH" },
            audit.Select(entry => entry.Action).ToArray());

        Assert.Throws<ContentConcurrencyException>(() =>
            service.UpdateDraft(
                draft.Key,
                CreatePackage(ContentStatus.Published, cost: 8m),
                "author.stale",
                "Attempt stale update",
                draft.StoreVersion,
                T0.AddMinutes(4)));
    }

    private static GameplayContentPackage CreatePackage(
        ContentStatus status,
        decimal cost = 3m,
        bool missingAbilityReference = false)
    {
        var catalog = GameplayRegistryCatalog.CreateSchemaV1();
        var card = new CardDefinition(
            Header(StableId.Parse("card.postgres_guardian"), status),
            CardType.Unit,
            new Classification(
                StableId.Parse("alignment.neutral"),
                StableId.Parse("class.control")),
            new StatBlock(new[]
            {
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.cost"), cost),
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.attack"), 2m),
                new KeyValuePair<StableId, decimal>(StableId.Parse("stat.max_health"), 5m)
            }),
            missingAbilityReference
                ? new[] { AbilityBinding.Reference(StableId.Parse("ability.missing")) }
                : Array.Empty<AbilityBinding>());

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
                            "Postgres Guardian",
                            "Durable content-store integration fixture.")
                    })
            });

        return new GameplayContentPackage(
            GameplaySchemaValidator.SupportedSchemaVersion,
            "authoring.postgres",
            T0,
            GameplayRegistrySnapshot.FromCatalog(catalog),
            Array.Empty<AbilityDefinition>(),
            new[] { card },
            presentation: presentation);
    }

    private static GameplayDefinitionHeader Header(
        StableId id,
        ContentStatus status) =>
        new(
            id,
            GameplaySchemaValidator.SupportedSchemaVersion,
            1,
            status,
            $"loc.{id.Value.Replace('.', '_')}");
}
