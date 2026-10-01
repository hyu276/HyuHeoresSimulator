/**
 * CONTENT_WORKFLOW_SERVICE
 * Purpose: Enforces Draft -> Review -> Approved -> Published transitions and materializes approved revisions through ContentPublisher.
 * Connections: Uses IContentRevisionRepository for persistence, GameplayContentPackageValidator for review gates, and ContentPublisher for immutable artifacts.
 * Risk: High because transition ordering, validation gates, and publish atomicity determine what content can reach production.
 */
using System;
using System.IO;
using System.Linq;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;
using HyuHeroes.Gameplay.Schema;

namespace HyuHeroes.Gameplay.Content.Workflow;

public sealed class ContentWorkflowService
{
    private readonly IContentRevisionRepository _repository;
    private readonly GameplayRegistryCatalog _catalog;

    public ContentWorkflowService(
        IContentRevisionRepository repository,
        GameplayRegistryCatalog? catalog = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _catalog = catalog ?? GameplayRegistryCatalog.CreateSchemaV1();
    }

    public ContentWorkspaceRevision CreateDraft(
        StableId workspaceId,
        GameplayContentPackage content,
        string actor,
        string reason,
        DateTimeOffset occurredAt)
    {
        if (workspaceId == default)
        {
            throw new ArgumentException("Workspace ID must be non-default.", nameof(workspaceId));
        }

        if (content is null) throw new ArgumentNullException(nameof(content));
        var latest = _repository.GetLatest(workspaceId);
        if (latest is not null && latest.State != ContentWorkflowState.Published)
        {
            throw new InvalidOperationException(
                $"Workspace '{workspaceId}' already has active revision '{latest.Key}' in state '{latest.State}'.");
        }

        var revisionNumber = latest is null ? 1 : checked(latest.Key.Revision + 1);
        var key = new ContentRevisionKey(workspaceId, revisionNumber);
        var normalized = MaterializeWorkflowState(content, ContentWorkflowState.Draft);
        var revision = new ContentWorkspaceRevision(
            key,
            ContentWorkflowState.Draft,
            normalized,
            occurredAt,
            actor,
            occurredAt,
            actor);

        return _repository.Save(
            revision,
            expectedStoreVersion: null,
            Audit(key, "CREATE_DRAFT", null, ContentWorkflowState.Draft, actor, occurredAt, reason));
    }

    public ContentWorkspaceRevision UpdateDraft(
        ContentRevisionKey key,
        GameplayContentPackage content,
        string actor,
        string reason,
        long expectedStoreVersion,
        DateTimeOffset occurredAt)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        var current = GetRequired(key);
        RequireState(current, ContentWorkflowState.Draft);

        var updated = current.With(
            content: MaterializeWorkflowState(content, ContentWorkflowState.Draft),
            updatedAt: occurredAt,
            updatedBy: actor,
            clearPublication: true);
        return _repository.Save(
            updated,
            expectedStoreVersion,
            Audit(key, "UPDATE_DRAFT", ContentWorkflowState.Draft, ContentWorkflowState.Draft, actor, occurredAt, reason));
    }

    public ContentWorkspaceRevision SubmitForReview(
        ContentRevisionKey key,
        string actor,
        string reason,
        long expectedStoreVersion,
        DateTimeOffset occurredAt)
    {
        var current = GetRequired(key);
        RequireState(current, ContentWorkflowState.Draft);

        var reviewContent = MaterializeWorkflowState(current.Content, ContentWorkflowState.Review);
        GameplayContentPackageValidator.ValidateForAuthoring(reviewContent, _catalog);
        return SaveTransition(
            current,
            ContentWorkflowState.Review,
            reviewContent,
            "SUBMIT_REVIEW",
            actor,
            reason,
            expectedStoreVersion,
            occurredAt);
    }

    public ContentWorkspaceRevision ReturnToDraft(
        ContentRevisionKey key,
        string actor,
        string reason,
        long expectedStoreVersion,
        DateTimeOffset occurredAt)
    {
        var current = GetRequired(key);
        if (current.State != ContentWorkflowState.Review &&
            current.State != ContentWorkflowState.Approved)
        {
            throw new InvalidOperationException(
                $"Revision '{key}' can return to Draft only from Review or Approved; current state is '{current.State}'.");
        }

        var draftContent = MaterializeWorkflowState(current.Content, ContentWorkflowState.Draft);
        return SaveTransition(
            current,
            ContentWorkflowState.Draft,
            draftContent,
            "RETURN_DRAFT",
            actor,
            reason,
            expectedStoreVersion,
            occurredAt);
    }

    public ContentWorkspaceRevision Approve(
        ContentRevisionKey key,
        string actor,
        string reason,
        long expectedStoreVersion,
        DateTimeOffset occurredAt)
    {
        var current = GetRequired(key);
        RequireState(current, ContentWorkflowState.Review);

        var approvedContent = MaterializeWorkflowState(current.Content, ContentWorkflowState.Approved);
        GameplayContentPackageValidator.ValidateForAuthoring(approvedContent, _catalog);
        return SaveTransition(
            current,
            ContentWorkflowState.Approved,
            approvedContent,
            "APPROVE",
            actor,
            reason,
            expectedStoreVersion,
            occurredAt);
    }

    public PreparedContentPublication PreparePublication(
        ContentRevisionKey key,
        ContentPublishRequest request,
        long expectedStoreVersion,
        DateTimeOffset occurredAt)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var current = GetRequired(key);
        RequireState(current, ContentWorkflowState.Approved);
        RequireStoreVersion(current, expectedStoreVersion);

        var publishable = MaterializePublished(
            current.Content,
            request.ContentVersion,
            occurredAt);
        var artifact = ContentPublisher.Publish(
            publishable,
            request.PackagePath,
            request.ExistingManifest,
            request.MakeDefault,
            _catalog);
        return new PreparedContentPublication(
            key,
            expectedStoreVersion,
            request,
            occurredAt,
            artifact);
    }

    public ContentWorkspaceRevision CommitDurablePublication(
        ContentRevisionKey key,
        GameplayContentPackage package,
        string packagePath,
        string actor,
        string reason,
        long expectedStoreVersion,
        DateTimeOffset publishedAt)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Package path cannot be empty.", nameof(packagePath));
        }

        var current = GetRequired(key);
        RequireState(current, ContentWorkflowState.Approved);
        RequireStoreVersion(current, expectedStoreVersion);
        ValidateDurablePackage(package, publishedAt);

        var hash = package.ContentHash
            ?? throw new InvalidOperationException("Published package is missing contentHash.");
        var publication = new ContentPublicationRecord(
            package.ContentVersion,
            hash,
            packagePath,
            publishedAt);
        var publishedRevision = current.With(
            state: ContentWorkflowState.Published,
            content: package,
            updatedAt: publishedAt,
            updatedBy: actor,
            publication: publication);

        return _repository.Save(
            publishedRevision,
            expectedStoreVersion,
            Audit(
                key,
                "PUBLISH",
                ContentWorkflowState.Approved,
                ContentWorkflowState.Published,
                actor,
                publishedAt,
                reason));
    }

    public ContentPublishResult Publish(
        ContentRevisionKey key,
        ContentPublishRequest request,
        string actor,
        string reason,
        long expectedStoreVersion,
        DateTimeOffset occurredAt)
    {
        var prepared = PreparePublication(
            key,
            request,
            expectedStoreVersion,
            occurredAt);
        var saved = CommitDurablePublication(
            key,
            prepared.Artifact.Package,
            prepared.Request.PackagePath,
            actor,
            reason,
            prepared.ExpectedStoreVersion,
            prepared.PublishedAt);
        return new ContentPublishResult(saved, prepared.Artifact);
    }

    private ContentWorkspaceRevision SaveTransition(
        ContentWorkspaceRevision current,
        ContentWorkflowState nextState,
        GameplayContentPackage content,
        string action,
        string actor,
        string reason,
        long expectedStoreVersion,
        DateTimeOffset occurredAt)
    {
        var updated = current.With(
            state: nextState,
            content: content,
            updatedAt: occurredAt,
            updatedBy: actor,
            clearPublication: true);
        return _repository.Save(
            updated,
            expectedStoreVersion,
            Audit(current.Key, action, current.State, nextState, actor, occurredAt, reason));
    }

    private ContentWorkspaceRevision GetRequired(ContentRevisionKey key) =>
        _repository.Get(key ?? throw new ArgumentNullException(nameof(key)))
        ?? throw new InvalidOperationException($"Content revision '{key}' does not exist.");

    private static void RequireStoreVersion(
        ContentWorkspaceRevision revision,
        long expectedStoreVersion)
    {
        if (revision.StoreVersion != expectedStoreVersion)
        {
            throw new ContentConcurrencyException(
                $"Revision '{revision.Key}' changed concurrently. Expected '{expectedStoreVersion}', actual '{revision.StoreVersion}'.");
        }
    }

    private void ValidateDurablePackage(
        GameplayContentPackage package,
        DateTimeOffset publishedAt)
    {
        if (package.ContentHash is not { } contentHash)
        {
            throw new InvalidDataException("Published package is missing contentHash.");
        }

        var expectedHash = GameplayContentCanonicalWriter.ComputeHash(package);
        if (!string.Equals(contentHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Published package hash mismatch. Expected '{expectedHash}', received '{contentHash}'.");
        }

        if (package.PublishedAt != publishedAt.ToUniversalTime())
        {
            throw new InvalidDataException(
                "Published package timestamp does not match the durable publication timestamp.");
        }

        GameplayContentPackageValidator.Validate(package, _catalog);
    }

    private static void RequireState(
        ContentWorkspaceRevision revision,
        ContentWorkflowState required)
    {
        if (revision.State != required)
        {
            throw new InvalidOperationException(
                $"Revision '{revision.Key}' must be '{required}' but is '{revision.State}'.");
        }
    }

    private static GameplayContentPackage MaterializeWorkflowState(
        GameplayContentPackage package,
        ContentWorkflowState state)
    {
        var status = ToContentStatus(state);
        return new GameplayContentPackage(
            package.SchemaVersion,
            package.ContentVersion,
            package.PublishedAt,
            package.Registries,
            package.Abilities.Select(ability => CloneAbility(ability, status)),
            package.Cards.Select(card => CloneCard(card, status)),
            contentHash: null,
            presentation: package.Presentation);
    }

    private static GameplayContentPackage MaterializePublished(
        GameplayContentPackage package,
        string contentVersion,
        DateTimeOffset publishedAt) =>
        new(
            package.SchemaVersion,
            contentVersion,
            publishedAt,
            package.Registries,
            package.Abilities.Select(ability => CloneAbility(ability, ContentStatus.Published)),
            package.Cards.Select(card => CloneCard(card, ContentStatus.Published)),
            contentHash: null,
            package.Presentation);

    private static AbilityDefinition CloneAbility(
        AbilityDefinition ability,
        ContentStatus status) =>
        new(
            CloneHeader(ability.Header, status),
            ability.Trigger,
            ability.Effects,
            ability.Condition,
            ability.UsageLimit,
            ability.Duration);

    private static CardDefinition CloneCard(
        CardDefinition card,
        ContentStatus status) =>
        new(
            CloneHeader(card.Header, status),
            card.CardType,
            card.Classification,
            card.BaseStats,
            card.Abilities.Select(binding =>
                binding.InlineAbility is { } inline
                    ? AbilityBinding.Inline(CloneAbility(inline, status))
                    : AbilityBinding.Reference(binding.ReferencedAbilityId
                        ?? throw new InvalidOperationException("Ability binding has neither reference nor inline definition."))),
            card.ArtworkReferenceId);

    private static GameplayDefinitionHeader CloneHeader(
        GameplayDefinitionHeader header,
        ContentStatus status) =>
        new(
            header.Id,
            header.SchemaVersion,
            header.Revision,
            status,
            header.LocalizationKey);

    private static ContentStatus ToContentStatus(ContentWorkflowState state) =>
        state switch
        {
            ContentWorkflowState.Draft => ContentStatus.Draft,
            ContentWorkflowState.Review => ContentStatus.Review,
            ContentWorkflowState.Approved => ContentStatus.Approved,
            ContentWorkflowState.Published => ContentStatus.Published,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported workflow state.")
        };

    private static ContentWorkflowAuditEntry Audit(
        ContentRevisionKey key,
        string action,
        ContentWorkflowState? fromState,
        ContentWorkflowState toState,
        string actor,
        DateTimeOffset occurredAt,
        string reason) =>
        new(0, key, action, fromState, toState, actor, occurredAt, reason);
}
