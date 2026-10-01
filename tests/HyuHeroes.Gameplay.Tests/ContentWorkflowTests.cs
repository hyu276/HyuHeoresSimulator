/**
 * CONTENT_WORKFLOW_TESTS
 * Purpose: Protects revision lifecycle, validation gates, optimistic concurrency, audit history, and publication through ContentPublisher.
 * Connections: Exercises ContentWorkflowService, IContentRevisionRepository, authoring validation, and immutable package publication.
 * Risk: High because workflow regressions could publish unreviewed content or overwrite concurrent admin edits.
 */
using System.Globalization;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Content.Workflow;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;
using HyuHeroes.Gameplay.Validation;

namespace HyuHeroes.Gameplay.Tests;

public sealed class ContentWorkflowTests
{
    private static readonly StableId WorkspaceId = StableId.Parse("workspace.prototype");
    private static readonly DateTimeOffset T0 =
        DateTimeOffset.Parse("2026-10-01T15:10:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Workflow_DraftReviewApprovedPublished_ProducesArtifactAndAudit()
    {
        var repository = new InMemoryContentRevisionRepository();
        var service = new ContentWorkflowService(repository);

        var draft = service.CreateDraft(
            WorkspaceId,
            CreatePackage(),
            "author.alice",
            "Start balance revision",
            T0);
        Assert.Equal(ContentWorkflowState.Draft, draft.State);
        Assert.Equal(ContentStatus.Draft, Assert.Single(draft.Content.Cards).Header.Status);
        Assert.Equal(1, draft.StoreVersion);

        var review = service.SubmitForReview(
            draft.Key,
            "author.alice",
            "Ready for review",
            draft.StoreVersion,
            T0.AddMinutes(1));
        var approved = service.Approve(
            review.Key,
            "reviewer.bob",
            "Balance review approved",
            review.StoreVersion,
            T0.AddMinutes(2));
        var published = service.Publish(
            approved.Key,
            new ContentPublishRequest(
                "prototype.workflow.1",
                "content/packages/prototype-workflow-1.json"),
            "publisher.carol",
            "Release approved revision",
            approved.StoreVersion,
            T0.AddMinutes(3));

        Assert.Equal(ContentWorkflowState.Published, published.Revision.State);
        Assert.Equal(ContentStatus.Published, Assert.Single(published.Revision.Content.Cards).Header.Status);
        Assert.Equal("prototype.workflow.1", published.Revision.Publication?.ContentVersion);
        Assert.Equal(published.Artifact.Package.ContentHash, published.Revision.Publication?.ContentHash);
        Assert.Equal(4, published.Revision.StoreVersion);

        var audit = repository.GetAudit(draft.Key);
        Assert.Equal(
            new[] { "CREATE_DRAFT", "SUBMIT_REVIEW", "APPROVE", "PUBLISH" },
            audit.Select(entry => entry.Action).ToArray());
        Assert.Equal(new long[] { 1, 2, 3, 4 }, audit.Select(entry => entry.Sequence).ToArray());
    }

    [Fact]
    public void PreparePublication_DoesNotMutateApprovedRevisionUntilDurableCommit()
    {
        var repository = new InMemoryContentRevisionRepository();
        var service = new ContentWorkflowService(repository);
        var draft = service.CreateDraft(
            WorkspaceId,
            CreatePackage(),
            "author.alice",
            "Create draft",
            T0);
        var review = service.SubmitForReview(
            draft.Key,
            "author.alice",
            "Review",
            draft.StoreVersion,
            T0.AddMinutes(1));
        var approved = service.Approve(
            review.Key,
            "reviewer.bob",
            "Approve",
            review.StoreVersion,
            T0.AddMinutes(2));

        var prepared = service.PreparePublication(
            approved.Key,
            new ContentPublishRequest(
                "prototype.workflow.prepared",
                "content/packages/prototype-workflow-prepared.json"),
            approved.StoreVersion,
            T0.AddMinutes(3));

        var stillApproved = repository.Get(approved.Key);
        Assert.NotNull(stillApproved);
        Assert.Equal(ContentWorkflowState.Approved, stillApproved.State);
        Assert.Null(stillApproved.Publication);
        Assert.Equal(approved.StoreVersion, stillApproved.StoreVersion);
        Assert.Equal(
            prepared.Artifact.Package.ContentHash,
            GameplayContentJsonLoader.Load(prepared.Artifact.PackageJson).ContentHash);

        var published = service.CommitDurablePublication(
            approved.Key,
            prepared.Artifact.Package,
            prepared.Request.PackagePath,
            "publisher.carol",
            "Artifact persisted",
            prepared.ExpectedStoreVersion,
            prepared.PublishedAt);

        Assert.Equal(ContentWorkflowState.Published, published.State);
        Assert.Equal(prepared.Artifact.Package.ContentHash, published.Publication?.ContentHash);
        Assert.Equal(approved.StoreVersion + 1, published.StoreVersion);
    }

    [Fact]
    public void CommitDurablePublication_RejectsTamperedPackageWithoutPublishing()
    {
        var repository = new InMemoryContentRevisionRepository();
        var service = new ContentWorkflowService(repository);
        var draft = service.CreateDraft(
            WorkspaceId,
            CreatePackage(),
            "author.alice",
            "Create draft",
            T0);
        var review = service.SubmitForReview(
            draft.Key,
            "author.alice",
            "Review",
            draft.StoreVersion,
            T0.AddMinutes(1));
        var approved = service.Approve(
            review.Key,
            "reviewer.bob",
            "Approve",
            review.StoreVersion,
            T0.AddMinutes(2));
        var prepared = service.PreparePublication(
            approved.Key,
            new ContentPublishRequest(
                "prototype.workflow.tamper",
                "content/packages/prototype-workflow-tamper.json"),
            approved.StoreVersion,
            T0.AddMinutes(3));
        var tampered = new GameplayContentPackage(
            prepared.Artifact.Package.SchemaVersion,
            prepared.Artifact.Package.ContentVersion,
            prepared.Artifact.Package.PublishedAt,
            prepared.Artifact.Package.Registries,
            prepared.Artifact.Package.Abilities,
            prepared.Artifact.Package.Cards,
            contentHash: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            presentation: prepared.Artifact.Package.Presentation);

        Assert.Throws<InvalidDataException>(() =>
            service.CommitDurablePublication(
                approved.Key,
                tampered,
                prepared.Request.PackagePath,
                "publisher.carol",
                "Attempt tampered commit",
                prepared.ExpectedStoreVersion,
                prepared.PublishedAt));

        var stored = repository.Get(approved.Key);
        Assert.NotNull(stored);
        Assert.Equal(ContentWorkflowState.Approved, stored.State);
        Assert.Null(stored.Publication);
    }

    [Fact]
    public void SubmitForReview_RejectsInvalidDraftWithoutChangingStoredRevision()
    {
        var repository = new InMemoryContentRevisionRepository();
        var service = new ContentWorkflowService(repository);
        var draft = service.CreateDraft(
            WorkspaceId,
            CreatePackage(missingAbilityReference: true),
            "author.alice",
            "Start incomplete revision",
            T0);

        Assert.Throws<InvalidDataException>(() =>
            service.SubmitForReview(
                draft.Key,
                "author.alice",
                "Try review",
                draft.StoreVersion,
                T0.AddMinutes(1)));

        var stored = repository.Get(draft.Key);
        Assert.NotNull(stored);
        Assert.Equal(ContentWorkflowState.Draft, stored.State);
        Assert.Equal(1, stored.StoreVersion);
        Assert.Single(repository.GetAudit(draft.Key));
    }

    [Fact]
    public void UpdateDraft_RejectsStaleStoreVersion()
    {
        var repository = new InMemoryContentRevisionRepository();
        var service = new ContentWorkflowService(repository);
        var draft = service.CreateDraft(
            WorkspaceId,
            CreatePackage(),
            "author.alice",
            "Create draft",
            T0);
        var updated = service.UpdateDraft(
            draft.Key,
            CreatePackage(cost: 4m),
            "author.alice",
            "Increase cost",
            draft.StoreVersion,
            T0.AddMinutes(1));

        var error = Assert.Throws<ContentConcurrencyException>(() =>
            service.UpdateDraft(
                draft.Key,
                CreatePackage(cost: 5m),
                "author.dave",
                "Concurrent edit",
                draft.StoreVersion,
                T0.AddMinutes(2)));

        Assert.Contains("changed concurrently", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, updated.StoreVersion);
        Assert.Equal(
            4m,
            Assert.Single(updated.Content.Cards).BaseStats.GetRequired(StableId.Parse("stat.cost")));
    }

    [Fact]
    public void PublishedRevision_IsImmutableAndNextDraftUsesNewRevisionNumber()
    {
        var repository = new InMemoryContentRevisionRepository();
        var service = new ContentWorkflowService(repository);
        var first = PublishOne(service);

        Assert.Throws<InvalidOperationException>(() =>
            service.UpdateDraft(
                first.Key,
                CreatePackage(cost: 7m),
                "author.alice",
                "Illegal published edit",
                first.StoreVersion,
                T0.AddMinutes(4)));

        var second = service.CreateDraft(
            WorkspaceId,
            first.Content,
            "author.alice",
            "Begin next release",
            T0.AddMinutes(5));

        Assert.Equal(2, second.Key.Revision);
        Assert.Equal(ContentWorkflowState.Draft, second.State);
        Assert.Null(second.Publication);
        Assert.Null(second.Content.ContentHash);
    }

    [Fact]
    public void ReturnToDraft_AllowsReviewFeedbackAndRequiresAnotherReview()
    {
        var repository = new InMemoryContentRevisionRepository();
        var service = new ContentWorkflowService(repository);
        var draft = service.CreateDraft(
            WorkspaceId,
            CreatePackage(),
            "author.alice",
            "Create draft",
            T0);
        var review = service.SubmitForReview(
            draft.Key,
            "author.alice",
            "Review request",
            draft.StoreVersion,
            T0.AddMinutes(1));
        var returned = service.ReturnToDraft(
            review.Key,
            "reviewer.bob",
            "Adjust card cost",
            review.StoreVersion,
            T0.AddMinutes(2));

        Assert.Equal(ContentWorkflowState.Draft, returned.State);
        Assert.Equal(ContentStatus.Draft, Assert.Single(returned.Content.Cards).Header.Status);
        Assert.Throws<InvalidOperationException>(() =>
            service.Approve(
                returned.Key,
                "reviewer.bob",
                "Cannot skip review",
                returned.StoreVersion,
                T0.AddMinutes(3)));
    }

    private static ContentWorkspaceRevision PublishOne(ContentWorkflowService service)
    {
        var draft = service.CreateDraft(
            WorkspaceId,
            CreatePackage(),
            "author.alice",
            "Create draft",
            T0);
        var review = service.SubmitForReview(
            draft.Key,
            "author.alice",
            "Review",
            draft.StoreVersion,
            T0.AddMinutes(1));
        var approved = service.Approve(
            review.Key,
            "reviewer.bob",
            "Approve",
            review.StoreVersion,
            T0.AddMinutes(2));
        return service.Publish(
            approved.Key,
            new ContentPublishRequest(
                "prototype.workflow.1",
                "content/packages/prototype-workflow-1.json"),
            "publisher.carol",
            "Publish",
            approved.StoreVersion,
            T0.AddMinutes(3)).Revision;
    }

    private static GameplayContentPackage CreatePackage(
        decimal cost = 3m,
        bool missingAbilityReference = false)
    {
        var catalog = GameplayRegistryCatalog.CreateSchemaV1();
        var cardId = StableId.Parse("card.workflow_guardian");
        var card = new CardDefinition(
            Header(cardId),
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
                            "Workflow Guardian",
                            "Workflow fixture card.")
                    })
            });

        return new GameplayContentPackage(
            GameplaySchemaValidator.SupportedSchemaVersion,
            "draft.workspace",
            T0,
            GameplayRegistrySnapshot.FromCatalog(catalog),
            Array.Empty<AbilityDefinition>(),
            new[] { card },
            presentation: presentation);
    }

    private static GameplayDefinitionHeader Header(StableId id) =>
        new(
            id,
            GameplaySchemaValidator.SupportedSchemaVersion,
            1,
            ContentStatus.Published,
            $"loc.{id.Value.Replace('.', '_')}");
}
