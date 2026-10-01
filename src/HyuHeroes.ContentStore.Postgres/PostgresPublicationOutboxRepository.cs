/**
 * POSTGRES_PUBLICATION_OUTBOX
 * Purpose: Persists prepared publication jobs with leasing, retry, and terminal-state semantics so artifact delivery survives process crashes.
 * Connections: Enqueues PreparedContentPublication while the revision is Approved; PublicationWorker leases jobs and advances durable stages.
 * Risk: High because outbox ordering and lease recovery determine whether publication can be resumed safely without duplicate or missing artifacts.
 */
using System.Data;
using HyuHeroes.Gameplay.Content.Workflow;
using Npgsql;
using NpgsqlTypes;

namespace HyuHeroes.ContentStore.Postgres;

public enum PublicationOutboxStage : short
{
    Queued = 0,
    PackageStored = 1,
    RevisionCommitted = 2,
    Completed = 3,
    Failed = 4
}

public sealed class PublicationOutboxJob
{
    public PublicationOutboxJob(
        Guid jobId,
        ContentRevisionKey key,
        long expectedStoreVersion,
        string actor,
        string reason,
        string contentVersion,
        string contentHash,
        string packagePath,
        string packageJson,
        bool makeDefault,
        DateTimeOffset publishedAt,
        PublicationOutboxStage stage,
        int attemptCount,
        DateTimeOffset availableAt,
        DateTimeOffset? leaseUntil,
        string? lastError,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job ID cannot be empty.", nameof(jobId));
        if (expectedStoreVersion <= 0) throw new ArgumentOutOfRangeException(nameof(expectedStoreVersion));
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (attemptCount < 0) throw new ArgumentOutOfRangeException(nameof(attemptCount));

        JobId = jobId;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        ExpectedStoreVersion = expectedStoreVersion;
        Actor = RequireText(actor, nameof(actor));
        Reason = RequireText(reason, nameof(reason));
        ContentVersion = RequireText(contentVersion, nameof(contentVersion));
        ContentHash = RequireText(contentHash, nameof(contentHash));
        PackagePath = RequireText(packagePath, nameof(packagePath));
        PackageJson = RequireText(packageJson, nameof(packageJson));
        MakeDefault = makeDefault;
        PublishedAt = publishedAt.ToUniversalTime();
        Stage = stage;
        AttemptCount = attemptCount;
        AvailableAt = availableAt.ToUniversalTime();
        LeaseUntil = leaseUntil?.ToUniversalTime();
        LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public Guid JobId { get; }
    public ContentRevisionKey Key { get; }
    public long ExpectedStoreVersion { get; }
    public string Actor { get; }
    public string Reason { get; }
    public string ContentVersion { get; }
    public string ContentHash { get; }
    public string PackagePath { get; }
    public string PackageJson { get; }
    public bool MakeDefault { get; }
    public DateTimeOffset PublishedAt { get; }
    public PublicationOutboxStage Stage { get; }
    public int AttemptCount { get; }
    public DateTimeOffset AvailableAt { get; }
    public DateTimeOffset? LeaseUntil { get; }
    public string? LastError { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
}

public sealed class PostgresPublicationOutboxRepository
{
    private const long LeaseAdvisoryLockId = 4_821_907_331L;

    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS content_publication_outbox (
            job_id uuid PRIMARY KEY,
            workspace_id text NOT NULL,
            revision integer NOT NULL CHECK (revision > 0),
            expected_store_version bigint NOT NULL CHECK (expected_store_version > 0),
            actor text NOT NULL,
            reason text NOT NULL,
            content_version text NOT NULL,
            content_hash text NOT NULL,
            package_path text NOT NULL,
            package_json text NOT NULL,
            make_default boolean NOT NULL,
            published_at timestamptz NOT NULL,
            stage smallint NOT NULL CHECK (stage BETWEEN 0 AND 4),
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_at timestamptz NOT NULL,
            lease_until timestamptz NULL,
            last_error text NULL,
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL,
            FOREIGN KEY (workspace_id, revision)
                REFERENCES content_revisions(workspace_id, revision)
                ON DELETE RESTRICT
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_content_publication_outbox_active_revision
            ON content_publication_outbox(workspace_id, revision)
            WHERE stage BETWEEN 0 AND 2;

        CREATE INDEX IF NOT EXISTS ix_content_publication_outbox_ready
            ON content_publication_outbox(stage, available_at, created_at);
        """;

    private const string Columns = """
        job_id, workspace_id, revision, expected_store_version,
        actor, reason, content_version, content_hash, package_path,
        package_json, make_default, published_at, stage, attempt_count,
        available_at, lease_until, last_error, created_at, updated_at
        """;

    private readonly string _connectionString;

    public PostgresPublicationOutboxRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("PostgreSQL connection string cannot be empty.", nameof(connectionString))
            : connectionString;
    }

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(SchemaSql, connection);
        command.ExecuteNonQuery();
    }

    public PublicationOutboxJob Enqueue(
        PreparedContentPublication prepared,
        string actor,
        string reason,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var contentHash = prepared.Artifact.Package.ContentHash
            ?? throw new InvalidOperationException("Prepared publication is missing contentHash.");
        var jobId = Guid.NewGuid();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        ValidateApprovedRevision(
            connection,
            transaction,
            prepared.Key,
            prepared.ExpectedStoreVersion);

        try
        {
            using var command = new NpgsqlCommand(
                """
                INSERT INTO content_publication_outbox (
                    job_id, workspace_id, revision, expected_store_version,
                    actor, reason, content_version, content_hash, package_path,
                    package_json, make_default, published_at, stage, attempt_count,
                    available_at, lease_until, last_error, created_at, updated_at)
                VALUES (
                    @job_id, @workspace_id, @revision, @expected_store_version,
                    @actor, @reason, @content_version, @content_hash, @package_path,
                    @package_json, @make_default, @published_at, @stage, 0,
                    @available_at, NULL, NULL, @created_at, @updated_at);
                """,
                connection,
                transaction);
            AddJobParameters(command, jobId, prepared, actor, reason, contentHash, now);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(
                $"Revision '{prepared.Key}' already has an active publication job.",
                error);
        }

        return Get(jobId)
            ?? throw new InvalidOperationException($"Publication job '{jobId}' was not persisted.");
    }

    public PublicationOutboxJob? Get(Guid jobId)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job ID cannot be empty.", nameof(jobId));
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM content_publication_outbox WHERE job_id = @job_id;",
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public PublicationOutboxJob? TryLeaseNext(
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        AcquireLeaseLock(connection, transaction);

        if (HasActiveLease(connection, transaction, now))
        {
            transaction.Commit();
            return null;
        }

        var jobId = FindReadyJob(connection, transaction, now);
        if (jobId is null)
        {
            transaction.Commit();
            return null;
        }

        using (var update = new NpgsqlCommand(
            """
            UPDATE content_publication_outbox
            SET attempt_count = attempt_count + 1,
                lease_until = @lease_until,
                updated_at = @updated_at
            WHERE job_id = @job_id;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("lease_until", now.Add(leaseDuration).UtcDateTime);
            update.Parameters.AddWithValue("updated_at", now.UtcDateTime);
            update.Parameters.AddWithValue("job_id", jobId.Value);
            update.ExecuteNonQuery();
        }

        var leased = ReadJob(connection, transaction, jobId.Value);
        transaction.Commit();
        return leased;
    }

    public void Advance(
        Guid jobId,
        PublicationOutboxStage expectedStage,
        PublicationOutboxStage nextStage,
        DateTimeOffset now)
    {
        if ((short)nextStage != (short)expectedStage + 1)
        {
            throw new ArgumentException("Outbox stage advances must be sequential.", nameof(nextStage));
        }

        var affected = UpdateStage(
            jobId,
            expectedStage,
            nextStage,
            now,
            lastError: null,
            availableAt: now);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Publication job '{jobId}' is no longer in expected stage '{expectedStage}'.");
        }
    }

    public void Retry(
        Guid jobId,
        PublicationOutboxStage expectedStage,
        string error,
        DateTimeOffset availableAt,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("Retry error cannot be empty.", nameof(error));
        }

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            UPDATE content_publication_outbox
            SET available_at = @available_at,
                lease_until = NULL,
                last_error = @last_error,
                updated_at = @updated_at
            WHERE job_id = @job_id AND stage = @expected_stage;
            """,
            connection);
        command.Parameters.AddWithValue("available_at", availableAt.UtcDateTime);
        command.Parameters.AddWithValue("last_error", error);
        command.Parameters.AddWithValue("updated_at", now.UtcDateTime);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("expected_stage", (short)expectedStage);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Publication job '{jobId}' is no longer in expected stage '{expectedStage}'.");
        }
    }

    public void Fail(
        Guid jobId,
        PublicationOutboxStage expectedStage,
        string error,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("Failure error cannot be empty.", nameof(error));
        }

        var affected = UpdateStage(
            jobId,
            expectedStage,
            PublicationOutboxStage.Failed,
            now,
            error,
            now);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Publication job '{jobId}' is no longer in expected stage '{expectedStage}'.");
        }
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void ValidateApprovedRevision(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ContentRevisionKey key,
        long expectedStoreVersion)
    {
        using var command = new NpgsqlCommand(
            """
            SELECT workflow_state, store_version
            FROM content_revisions
            WHERE workspace_id = @workspace_id AND revision = @revision
            FOR UPDATE;
            """,
            connection,
            transaction);
        AddKey(command, key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Content revision '{key}' does not exist.");
        }

        var state = (ContentWorkflowState)reader.GetInt16(0);
        var storeVersion = reader.GetInt64(1);
        if (state != ContentWorkflowState.Approved)
        {
            throw new InvalidOperationException(
                $"Revision '{key}' must be Approved before publication can be queued; current state is '{state}'.");
        }

        if (storeVersion != expectedStoreVersion)
        {
            throw new ContentConcurrencyException(
                $"Revision '{key}' changed concurrently. Expected '{expectedStoreVersion}', actual '{storeVersion}'.");
        }
    }

    private static void AddJobParameters(
        NpgsqlCommand command,
        Guid jobId,
        PreparedContentPublication prepared,
        string actor,
        string reason,
        string contentHash,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("job_id", jobId);
        AddKey(command, prepared.Key);
        command.Parameters.AddWithValue("expected_store_version", prepared.ExpectedStoreVersion);
        command.Parameters.AddWithValue("actor", RequireText(actor, nameof(actor)));
        command.Parameters.AddWithValue("reason", RequireText(reason, nameof(reason)));
        command.Parameters.AddWithValue("content_version", prepared.Artifact.Package.ContentVersion);
        command.Parameters.AddWithValue("content_hash", contentHash);
        command.Parameters.AddWithValue("package_path", prepared.Request.PackagePath);
        command.Parameters.AddWithValue("package_json", prepared.Artifact.PackageJson);
        command.Parameters.AddWithValue("make_default", prepared.Request.MakeDefault);
        command.Parameters.AddWithValue("published_at", prepared.PublishedAt.UtcDateTime);
        command.Parameters.AddWithValue("stage", (short)PublicationOutboxStage.Queued);
        command.Parameters.AddWithValue("available_at", now.UtcDateTime);
        command.Parameters.AddWithValue("created_at", now.UtcDateTime);
        command.Parameters.AddWithValue("updated_at", now.UtcDateTime);
    }

    private static void AcquireLeaseLock(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_id);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_id", LeaseAdvisoryLockId);
        command.ExecuteNonQuery();
    }

    private static bool HasActiveLease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now)
    {
        using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM content_publication_outbox
                WHERE stage BETWEEN 0 AND 2
                  AND lease_until > @now);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    private static Guid? FindReadyJob(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now)
    {
        using var command = new NpgsqlCommand(
            """
            SELECT job_id
            FROM content_publication_outbox
            WHERE stage BETWEEN 0 AND 2
              AND available_at <= @now
              AND (lease_until IS NULL OR lease_until <= @now)
            ORDER BY created_at, job_id
            FOR UPDATE SKIP LOCKED
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        var value = command.ExecuteScalar();
        return value is Guid jobId ? jobId : null;
    }

    private static PublicationOutboxJob ReadJob(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId)
    {
        using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM content_publication_outbox WHERE job_id = @job_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("job_id", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadJob(reader)
            : throw new InvalidOperationException($"Publication job '{jobId}' disappeared while leased.");
    }

    private static PublicationOutboxJob ReadJob(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            new ContentRevisionKey(
                HyuHeroes.Gameplay.Core.StableId.Parse(reader.GetString(1)),
                reader.GetInt32(2)),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetBoolean(10),
            AsUtcOffset(reader.GetDateTime(11)),
            (PublicationOutboxStage)reader.GetInt16(12),
            reader.GetInt32(13),
            AsUtcOffset(reader.GetDateTime(14)),
            reader.IsDBNull(15) ? null : AsUtcOffset(reader.GetDateTime(15)),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            AsUtcOffset(reader.GetDateTime(17)),
            AsUtcOffset(reader.GetDateTime(18)));

    private int UpdateStage(
        Guid jobId,
        PublicationOutboxStage expectedStage,
        PublicationOutboxStage nextStage,
        DateTimeOffset now,
        string? lastError,
        DateTimeOffset availableAt)
    {
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            UPDATE content_publication_outbox
            SET stage = @next_stage,
                available_at = @available_at,
                lease_until = NULL,
                last_error = @last_error,
                updated_at = @updated_at
            WHERE job_id = @job_id AND stage = @expected_stage;
            """,
            connection);
        command.Parameters.AddWithValue("next_stage", (short)nextStage);
        command.Parameters.AddWithValue("available_at", availableAt.UtcDateTime);
        command.Parameters.AddWithValue(
            "last_error",
            NpgsqlDbType.Text,
            lastError is null ? DBNull.Value : lastError);
        command.Parameters.AddWithValue("updated_at", now.UtcDateTime);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("expected_stage", (short)expectedStage);
        return command.ExecuteNonQuery();
    }

    private static void AddKey(NpgsqlCommand command, ContentRevisionKey key)
    {
        command.Parameters.AddWithValue("workspace_id", key.WorkspaceId.Value);
        command.Parameters.AddWithValue("revision", key.Revision);
    }

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
}
