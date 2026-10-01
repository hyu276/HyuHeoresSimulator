/**
 * POSTGRES_CONTENT_REVISION_REPOSITORY
 * Purpose: Provides durable PostgreSQL persistence for authoring revisions while preserving the workflow repository's optimistic-concurrency and atomic audit contract.
 * Connections: Implements IContentRevisionRepository; serializes GameplayContentPackage snapshots with the canonical writer and reloads them through LoadAuthoringSnapshot.
 * Risk: High because transaction ordering, storeVersion checks, and revision/audit atomicity protect content history from lost updates.
 */
using System.Data;
using HyuHeroes.Gameplay.Content;
using HyuHeroes.Gameplay.Content.Workflow;
using HyuHeroes.Gameplay.Core;
using Npgsql;
using NpgsqlTypes;

namespace HyuHeroes.ContentStore.Postgres;

public static class PostgresContentRevisionSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS content_revisions (
            workspace_id text NOT NULL,
            revision integer NOT NULL CHECK (revision > 0),
            workflow_state smallint NOT NULL CHECK (workflow_state BETWEEN 0 AND 3),
            content_json jsonb NOT NULL,
            created_at timestamptz NOT NULL,
            created_by text NOT NULL,
            updated_at timestamptz NOT NULL,
            updated_by text NOT NULL,
            store_version bigint NOT NULL CHECK (store_version > 0),
            publication_content_version text NULL,
            publication_content_hash text NULL,
            publication_package_path text NULL,
            publication_published_at timestamptz NULL,
            PRIMARY KEY (workspace_id, revision),
            CHECK (
                (workflow_state = 3 AND publication_content_version IS NOT NULL AND publication_content_hash IS NOT NULL
                    AND publication_package_path IS NOT NULL AND publication_published_at IS NOT NULL)
                OR
                (workflow_state <> 3 AND publication_content_version IS NULL AND publication_content_hash IS NULL
                    AND publication_package_path IS NULL AND publication_published_at IS NULL)
            )
        );

        CREATE TABLE IF NOT EXISTS content_revision_audit (
            workspace_id text NOT NULL,
            revision integer NOT NULL,
            sequence bigint NOT NULL CHECK (sequence > 0),
            action text NOT NULL,
            from_state smallint NULL,
            to_state smallint NOT NULL CHECK (to_state BETWEEN 0 AND 3),
            actor text NOT NULL,
            occurred_at timestamptz NOT NULL,
            reason text NOT NULL,
            PRIMARY KEY (workspace_id, revision, sequence),
            FOREIGN KEY (workspace_id, revision)
                REFERENCES content_revisions(workspace_id, revision)
                ON DELETE RESTRICT
        );

        CREATE INDEX IF NOT EXISTS ix_content_revisions_latest
            ON content_revisions(workspace_id, revision DESC);
        """;
}

public sealed class PostgresContentRevisionRepository : IContentRevisionRepository
{
    private const string RevisionColumns = """
        workspace_id, revision, workflow_state, content_json::text,
        created_at, created_by, updated_at, updated_by, store_version,
        publication_content_version, publication_content_hash,
        publication_package_path, publication_published_at
        """;

    private readonly string _connectionString;

    public PostgresContentRevisionRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("PostgreSQL connection string cannot be empty.", nameof(connectionString))
            : connectionString;
    }

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(PostgresContentRevisionSchema.Sql, connection);
        command.ExecuteNonQuery();
    }

    public ContentWorkspaceRevision? Get(ContentRevisionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            $"SELECT {RevisionColumns} FROM content_revisions WHERE workspace_id = @workspace_id AND revision = @revision;",
            connection);
        AddKey(command, key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRevision(reader) : null;
    }

    public ContentWorkspaceRevision? GetLatest(StableId workspaceId)
    {
        RequireWorkspaceId(workspaceId);
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            $"SELECT {RevisionColumns} FROM content_revisions WHERE workspace_id = @workspace_id ORDER BY revision DESC LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("workspace_id", workspaceId.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRevision(reader) : null;
    }

    public IReadOnlyList<ContentWorkspaceRevision> List(StableId workspaceId)
    {
        RequireWorkspaceId(workspaceId);
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            $"SELECT {RevisionColumns} FROM content_revisions WHERE workspace_id = @workspace_id ORDER BY revision;",
            connection);
        command.Parameters.AddWithValue("workspace_id", workspaceId.Value);
        using var reader = command.ExecuteReader();

        var revisions = new List<ContentWorkspaceRevision>();
        while (reader.Read())
        {
            revisions.Add(ReadRevision(reader));
        }

        return revisions.AsReadOnly();
    }

    public IReadOnlyList<ContentWorkflowAuditEntry> GetAudit(ContentRevisionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(
            """
            SELECT sequence, action, from_state, to_state, actor, occurred_at, reason
            FROM content_revision_audit
            WHERE workspace_id = @workspace_id AND revision = @revision
            ORDER BY sequence;
            """,
            connection);
        AddKey(command, key);
        using var reader = command.ExecuteReader();

        var entries = new List<ContentWorkflowAuditEntry>();
        while (reader.Read())
        {
            entries.Add(ReadAudit(reader, key));
        }

        return entries.AsReadOnly();
    }

    public ContentWorkspaceRevision Save(
        ContentWorkspaceRevision revision,
        long? expectedStoreVersion,
        ContentWorkflowAuditEntry auditEntry)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(auditEntry);
        if (!revision.Key.Equals(auditEntry.Key))
        {
            throw new ArgumentException("Revision and audit entry must use the same key.", nameof(auditEntry));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        var currentStoreVersion = LockStoreVersion(connection, transaction, revision.Key);
        ValidateConcurrency(revision.Key, currentStoreVersion, expectedStoreVersion);

        var nextStoreVersion = currentStoreVersion is { } current
            ? checked(current + 1)
            : 1;
        WriteRevision(connection, transaction, revision, nextStoreVersion, currentStoreVersion is not null);
        AppendAudit(connection, transaction, auditEntry);
        transaction.Commit();
        return revision.With(storeVersion: nextStoreVersion);
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static long? LockStoreVersion(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ContentRevisionKey key)
    {
        using var command = new NpgsqlCommand(
            """
            SELECT store_version
            FROM content_revisions
            WHERE workspace_id = @workspace_id AND revision = @revision
            FOR UPDATE;
            """,
            connection,
            transaction);
        AddKey(command, key);
        var result = command.ExecuteScalar();
        return result is null || result is DBNull ? null : Convert.ToInt64(result);
    }

    private static void ValidateConcurrency(
        ContentRevisionKey key,
        long? currentStoreVersion,
        long? expectedStoreVersion)
    {
        if (currentStoreVersion is null)
        {
            if (expectedStoreVersion is not null)
            {
                throw new ContentConcurrencyException(
                    $"Revision '{key}' does not exist but expected store version '{expectedStoreVersion}'.");
            }

            return;
        }

        if (expectedStoreVersion is null || currentStoreVersion.Value != expectedStoreVersion.Value)
        {
            throw new ContentConcurrencyException(
                $"Revision '{key}' changed concurrently. Expected '{expectedStoreVersion?.ToString() ?? "<new>"}', actual '{currentStoreVersion}'.");
        }
    }

    private static void WriteRevision(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ContentWorkspaceRevision revision,
        long nextStoreVersion,
        bool exists)
    {
        var sql = exists
            ? """
              UPDATE content_revisions
              SET workflow_state = @workflow_state,
                  content_json = @content_json,
                  created_at = @created_at,
                  created_by = @created_by,
                  updated_at = @updated_at,
                  updated_by = @updated_by,
                  store_version = @store_version,
                  publication_content_version = @publication_content_version,
                  publication_content_hash = @publication_content_hash,
                  publication_package_path = @publication_package_path,
                  publication_published_at = @publication_published_at
              WHERE workspace_id = @workspace_id AND revision = @revision;
              """
            : """
              INSERT INTO content_revisions (
                  workspace_id, revision, workflow_state, content_json,
                  created_at, created_by, updated_at, updated_by, store_version,
                  publication_content_version, publication_content_hash,
                  publication_package_path, publication_published_at)
              VALUES (
                  @workspace_id, @revision, @workflow_state, @content_json,
                  @created_at, @created_by, @updated_at, @updated_by, @store_version,
                  @publication_content_version, @publication_content_hash,
                  @publication_package_path, @publication_published_at);
              """;

        using var command = new NpgsqlCommand(sql, connection, transaction);
        AddKey(command, revision.Key);
        command.Parameters.AddWithValue("workflow_state", (short)revision.State);
        command.Parameters.AddWithValue(
            "content_json",
            NpgsqlDbType.Jsonb,
            GameplayContentCanonicalWriter.Serialize(revision.Content, indented: false));
        command.Parameters.AddWithValue("created_at", revision.CreatedAt.UtcDateTime);
        command.Parameters.AddWithValue("created_by", revision.CreatedBy);
        command.Parameters.AddWithValue("updated_at", revision.UpdatedAt.UtcDateTime);
        command.Parameters.AddWithValue("updated_by", revision.UpdatedBy);
        command.Parameters.AddWithValue("store_version", nextStoreVersion);
        AddPublicationParameters(command, revision.Publication);
        command.ExecuteNonQuery();
    }

    private static void AddPublicationParameters(
        NpgsqlCommand command,
        ContentPublicationRecord? publication)
    {
        AddNullableText(command, "publication_content_version", publication?.ContentVersion);
        AddNullableText(command, "publication_content_hash", publication?.ContentHash);
        AddNullableText(command, "publication_package_path", publication?.PackagePath);
        command.Parameters.AddWithValue(
            "publication_published_at",
            NpgsqlDbType.TimestampTz,
            publication is null ? DBNull.Value : publication.PublishedAt.UtcDateTime);
    }

    private static void AppendAudit(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ContentWorkflowAuditEntry auditEntry)
    {
        var sequence = GetNextAuditSequence(connection, transaction, auditEntry.Key);
        using var command = new NpgsqlCommand(
            """
            INSERT INTO content_revision_audit (
                workspace_id, revision, sequence, action, from_state,
                to_state, actor, occurred_at, reason)
            VALUES (
                @workspace_id, @revision, @sequence, @action, @from_state,
                @to_state, @actor, @occurred_at, @reason);
            """,
            connection,
            transaction);
        AddKey(command, auditEntry.Key);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("action", auditEntry.Action);
        command.Parameters.AddWithValue(
            "from_state",
            NpgsqlDbType.Smallint,
            auditEntry.FromState is null ? DBNull.Value : (short)auditEntry.FromState.Value);
        command.Parameters.AddWithValue("to_state", (short)auditEntry.ToState);
        command.Parameters.AddWithValue("actor", auditEntry.Actor);
        command.Parameters.AddWithValue("occurred_at", auditEntry.OccurredAt.UtcDateTime);
        command.Parameters.AddWithValue("reason", auditEntry.Reason);
        command.ExecuteNonQuery();
    }

    private static long GetNextAuditSequence(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ContentRevisionKey key)
    {
        using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(MAX(sequence), 0) + 1
            FROM content_revision_audit
            WHERE workspace_id = @workspace_id AND revision = @revision;
            """,
            connection,
            transaction);
        AddKey(command, key);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static ContentWorkspaceRevision ReadRevision(NpgsqlDataReader reader)
    {
        var key = new ContentRevisionKey(
            StableId.Parse(reader.GetString(0)),
            reader.GetInt32(1));
        var state = (ContentWorkflowState)reader.GetInt16(2);
        var content = GameplayContentJsonLoader.ParseAuthoringSnapshot(reader.GetString(3));
        var publication = ReadPublication(reader, state);

        return new ContentWorkspaceRevision(
            key,
            state,
            content,
            AsUtcOffset(reader.GetDateTime(4)),
            reader.GetString(5),
            AsUtcOffset(reader.GetDateTime(6)),
            reader.GetString(7),
            reader.GetInt64(8),
            publication);
    }

    private static ContentPublicationRecord? ReadPublication(
        NpgsqlDataReader reader,
        ContentWorkflowState state)
    {
        if (state != ContentWorkflowState.Published)
        {
            return null;
        }

        return new ContentPublicationRecord(
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            AsUtcOffset(reader.GetDateTime(12)));
    }

    private static ContentWorkflowAuditEntry ReadAudit(
        NpgsqlDataReader reader,
        ContentRevisionKey key)
    {
        var fromState = reader.IsDBNull(2)
            ? (ContentWorkflowState?)null
            : (ContentWorkflowState)reader.GetInt16(2);
        return new ContentWorkflowAuditEntry(
            reader.GetInt64(0),
            key,
            reader.GetString(1),
            fromState,
            (ContentWorkflowState)reader.GetInt16(3),
            reader.GetString(4),
            AsUtcOffset(reader.GetDateTime(5)),
            reader.GetString(6));
    }

    private static void AddKey(NpgsqlCommand command, ContentRevisionKey key)
    {
        command.Parameters.AddWithValue("workspace_id", key.WorkspaceId.Value);
        command.Parameters.AddWithValue("revision", key.Revision);
    }

    private static void AddNullableText(
        NpgsqlCommand command,
        string parameterName,
        string? value) =>
        command.Parameters.AddWithValue(
            parameterName,
            NpgsqlDbType.Text,
            value is null ? DBNull.Value : value);

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static void RequireWorkspaceId(StableId workspaceId)
    {
        if (workspaceId == default)
        {
            throw new ArgumentException("Workspace ID must be non-default.", nameof(workspaceId));
        }
    }
}
