/**
 * FILE_CONTENT_REVISION_REPOSITORY_TESTS
 * Purpose: Protects durable authoring revision round-trips, audit persistence, cross-instance optimistic concurrency, and published package integrity.
 * Connections: Exercises FileContentRevisionRepository through ContentWorkflowService and the trusted published-package loader.
 * Risk: High because persistence regressions could lose audit history, accept stale edits, or reload a published artifact without verification.
 */
using System.Globalization;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Content.Workflow;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class FileContentRevisionRepositoryTests
{
    private static readonly StableId WorkspaceId = StableId.Parse("workspace.file_repo");
    private static readonly DateTimeOffset T0 =
        DateTimeOffset.Parse("2026-10-01T15:20:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Repository_RoundTripsDraftAndAuditAcrossInstances()
    {
        var path = TemporaryRepositoryPath();
        try
        {
            var firstService = new ContentWorkflowService(new FileContentRevisionRepository(path));
            var draft = firstService.CreateDraft(
                WorkspaceId,
                CreatePackage(),
                "author.alice",
                "Persist draft",
                T0);

            var secondRepository = new FileContentRevisionRepository(path);
            var loaded = secondRepository.Get(draft.Key);

            Assert.NotNull(loaded);
            Assert.Equal(ContentWorkflowState.Draft, loaded.State);
            Assert.Equal(1, loaded.StoreVersion);
            Assert.Equal(ContentStatus.Draft, Assert.Single(loaded.Content.Cards).Header.Status);
            var audit = Assert.Single(secondRepository.GetAudit(draft.Key));
            Assert.Equal("CREATE_DRAFT", audit.Action);
            Assert.Equal("author.alice", audit.Actor);
            Assert.Equal("Persist draft", audit.Reason);
        }
        finally
        {
            DeleteRepositoryFiles(path);
        }
    }

    [Fact]
    public void Repository_PersistsPublishedArtifactAndReloadsTrustedPackage()
    {
        var path = TemporaryRepositoryPath();
        try
        {
            var service = new ContentWorkflowService(new FileContentRevisionRepository(path));
            var draft = service.CreateDraft(
                WorkspaceId,
                CreatePackage(),
                "author.alice",
                "Create release",
                T0);
            var review = service.SubmitForReview(
                draft.Key,
                "author.alice",
                "Ready",
                draft.StoreVersion,
                T0.AddMinutes(1));
            var approved = service.Approve(
                review.Key,
                "reviewer.bob",
                "Approved",
                review.StoreVersion,
                T0.AddMinutes(2));
            var published = service.Publish(
                approved.Key,
                new ContentPublishRequest(
                    "prototype.file.1",
                    "content/packages/prototype-file-1.json"),
                "publisher.carol",
                "Publish file-backed revision",
                approved.StoreVersion,
                T0.AddMinutes(3));

            var reloaded = new FileContentRevisionRepository(path).Get(published.Revision.Key);

            Assert.NotNull(reloaded);
            Assert.Equal(ContentWorkflowState.Published, reloaded.State);
            Assert.Equal(published.Artifact.Package.ContentHash, reloaded.Content.ContentHash);
            Assert.Equal(published.Artifact.Package.ContentHash, reloaded.Publication?.ContentHash);
            Assert.Equal(4, reloaded.StoreVersion);
        }
        finally
        {
            DeleteRepositoryFiles(path);
        }
    }

    [Fact]
    public void Repository_DetectsStaleSaveAcrossSeparateInstances()
    {
        var path = TemporaryRepositoryPath();
        try
        {
            var serviceA = new ContentWorkflowService(new FileContentRevisionRepository(path));
            var draft = serviceA.CreateDraft(
                WorkspaceId,
                CreatePackage(),
                "author.alice",
                "Create draft",
                T0);

            var repositoryB = new FileContentRevisionRepository(path);
            var stale = repositoryB.Get(draft.Key);
            Assert.NotNull(stale);

            var updated = serviceA.UpdateDraft(
                draft.Key,
                CreatePackage(cost: 4m),
                "author.alice",
                "First edit",
                draft.StoreVersion,
                T0.AddMinutes(1));
            Assert.Equal(2, updated.StoreVersion);

            var serviceB = new ContentWorkflowService(repositoryB);
            Assert.Throws<ContentConcurrencyException>(() =>
                serviceB.UpdateDraft(
                    draft.Key,
                    CreatePackage(cost: 5m),
                    "author.dave",
                    "Stale edit",
                    stale.StoreVersion,
                    T0.AddMinutes(2)));
        }
        finally
        {
            DeleteRepositoryFiles(path);
        }
    }

    [Fact]
    public void Repository_FileContainsRevisionAndAuditAsOneDocument()
    {
        var path = TemporaryRepositoryPath();
        try
        {
            var service = new ContentWorkflowService(new FileContentRevisionRepository(path));
            service.CreateDraft(
                WorkspaceId,
                CreatePackage(),
                "author.alice",
                "Create durable record",
                T0);

            var json = File.ReadAllText(path);

            Assert.Contains("\"repositoryFormatVersion\": 1", json, StringComparison.Ordinal);
            Assert.Contains("\"revisions\":", json, StringComparison.Ordinal);
            Assert.Contains("\"audit\":", json, StringComparison.Ordinal);
            Assert.Contains("CREATE_DRAFT", json, StringComparison.Ordinal);
            Assert.Contains("Create durable record", json, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRepositoryFiles(path);
        }
    }

    private static GameplayContentPackage CreatePackage(decimal cost = 3m)
    {
        var catalog = GameplayRegistryCatalog.CreateSchemaV1();
        var cardId = StableId.Parse("card.file_guardian");
        var card = new CardDefinition(
            new GameplayDefinitionHeader(
                cardId,
                GameplaySchemaValidator.SupportedSchemaVersion,
                1,
                ContentStatus.Published,
                "loc.card_file_guardian"),
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
                            "File Guardian",
                            "File repository fixture card.")
                    })
            });

        return new GameplayContentPackage(
            GameplaySchemaValidator.SupportedSchemaVersion,
            "draft.file",
            T0,
            GameplayRegistrySnapshot.FromCatalog(catalog),
            Array.Empty<AbilityDefinition>(),
            new[] { card },
            presentation: presentation);
    }

    private static string TemporaryRepositoryPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "hyuheroes-content-tests",
            Guid.NewGuid().ToString("N"),
            "repository.json");

    private static void DeleteRepositoryFiles(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
