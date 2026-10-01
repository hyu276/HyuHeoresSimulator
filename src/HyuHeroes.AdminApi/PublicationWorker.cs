/**
 * PUBLICATION_WORKER
 * Purpose: Drains durable publication jobs one stage at a time so package storage precedes Published state and manifest promotion follows it.
 * Connections: Leases PostgresPublicationOutboxRepository jobs, writes IContentArtifactStore artifacts, and commits via ContentWorkflowService.
 * Risk: High because stage ordering is the guarantee that a Published revision never points at a missing package.
 */
using HyuHeroes.ContentStore.Postgres;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Content.Workflow;

namespace HyuHeroes.AdminApi;

public sealed class PublicationWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private const int MaximumAttempts = 8;

    private readonly PostgresPublicationOutboxRepository _outbox;
    private readonly IContentRevisionRepository _revisions;
    private readonly ContentWorkflowService _workflow;
    private readonly IContentArtifactStore _artifacts;
    private readonly ILogger<PublicationWorker> _logger;

    public PublicationWorker(
        PostgresPublicationOutboxRepository outbox,
        IContentRevisionRepository revisions,
        ContentWorkflowService workflow,
        IContentArtifactStore artifacts,
        ILogger<PublicationWorker> logger)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PublicationOutboxJob? job;
            try
            {
                job = _outbox.TryLeaseNext(DateTimeOffset.UtcNow, LeaseDuration);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogError(error, "Failed to lease publication outbox job.");
                await Task.Delay(ErrorDelay, stoppingToken);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            await ProcessLeasedJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessLeasedJobAsync(
        PublicationOutboxJob job,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessStageAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsTerminal(error))
        {
            _logger.LogWarning(
                error,
                "Publication job {JobId} failed permanently at stage {Stage}.",
                job.JobId,
                job.Stage);
            _outbox.Fail(
                job.JobId,
                job.Stage,
                error.Message,
                DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            await ScheduleRetryAsync(job, error, cancellationToken);
        }
    }

    private async Task ProcessStageAsync(
        PublicationOutboxJob job,
        CancellationToken cancellationToken)
    {
        switch (job.Stage)
        {
            case PublicationOutboxStage.Queued:
                await StorePackageAsync(job, cancellationToken);
                return;
            case PublicationOutboxStage.PackageStored:
                CommitRevision(job);
                return;
            case PublicationOutboxStage.RevisionCommitted:
                await PromoteManifestAsync(job, cancellationToken);
                return;
            default:
                throw new InvalidOperationException(
                    $"Leased publication job '{job.JobId}' has non-processable stage '{job.Stage}'.");
        }
    }

    private async Task StorePackageAsync(
        PublicationOutboxJob job,
        CancellationToken cancellationToken)
    {
        await _artifacts.StorePackageAsync(
            job.PackagePath,
            job.PackageJson,
            job.ContentHash,
            cancellationToken);
        _outbox.Advance(
            job.JobId,
            PublicationOutboxStage.Queued,
            PublicationOutboxStage.PackageStored,
            DateTimeOffset.UtcNow);
    }

    private void CommitRevision(PublicationOutboxJob job)
    {
        var current = _revisions.Get(job.Key)
            ?? throw new InvalidOperationException(
                $"Content revision '{job.Key}' disappeared while publication was queued.");

        if (current.State == ContentWorkflowState.Published)
        {
            ValidateAlreadyCommitted(current, job);
        }
        else
        {
            var package = GameplayContentJsonLoader.Load(job.PackageJson);
            _workflow.CommitDurablePublication(
                job.Key,
                package,
                job.PackagePath,
                job.Actor,
                job.Reason,
                job.ExpectedStoreVersion,
                job.PublishedAt);
        }

        _outbox.Advance(
            job.JobId,
            PublicationOutboxStage.PackageStored,
            PublicationOutboxStage.RevisionCommitted,
            DateTimeOffset.UtcNow);
    }

    private async Task PromoteManifestAsync(
        PublicationOutboxJob job,
        CancellationToken cancellationToken)
    {
        var package = GameplayContentJsonLoader.Load(job.PackageJson);
        await _artifacts.PromoteManifestAsync(
            package,
            job.PackagePath,
            job.MakeDefault,
            cancellationToken);
        _outbox.Advance(
            job.JobId,
            PublicationOutboxStage.RevisionCommitted,
            PublicationOutboxStage.Completed,
            DateTimeOffset.UtcNow);
    }

    private static void ValidateAlreadyCommitted(
        ContentWorkspaceRevision revision,
        PublicationOutboxJob job)
    {
        var publication = revision.Publication
            ?? throw new InvalidOperationException(
                $"Published revision '{revision.Key}' is missing publication metadata.");
        if (!string.Equals(publication.ContentHash, job.ContentHash, StringComparison.Ordinal) ||
            !string.Equals(publication.ContentVersion, job.ContentVersion, StringComparison.Ordinal) ||
            !string.Equals(publication.PackagePath, job.PackagePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Published revision '{revision.Key}' does not match queued publication job '{job.JobId}'.");
        }
    }

    private async Task ScheduleRetryAsync(
        PublicationOutboxJob job,
        Exception error,
        CancellationToken cancellationToken)
    {
        if (job.AttemptCount >= MaximumAttempts)
        {
            _logger.LogError(
                error,
                "Publication job {JobId} exhausted retry attempts at stage {Stage}.",
                job.JobId,
                job.Stage);
            _outbox.Fail(
                job.JobId,
                job.Stage,
                error.Message,
                DateTimeOffset.UtcNow);
            return;
        }

        var retryDelay = TimeSpan.FromSeconds(
            Math.Min(300, Math.Pow(2, Math.Max(0, job.AttemptCount - 1))));
        _logger.LogWarning(
            error,
            "Publication job {JobId} will retry stage {Stage} after {Delay}.",
            job.JobId,
            job.Stage,
            retryDelay);
        var now = DateTimeOffset.UtcNow;
        _outbox.Retry(
            job.JobId,
            job.Stage,
            error.Message,
            now.Add(retryDelay),
            now);
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsTerminal(Exception error) =>
        error is ContentConcurrencyException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException;
}
