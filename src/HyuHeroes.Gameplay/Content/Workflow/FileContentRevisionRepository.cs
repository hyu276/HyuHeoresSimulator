/**
 * FILE_CONTENT_REVISION_REPOSITORY
 * Purpose: Persists content workflow revisions and audit history to a durable JSON document for the prototype Admin Dashboard/backend.
 * Connections: Implements IContentRevisionRepository; stores package snapshots through canonical JSON and reloads published snapshots through the trusted package loader.
 * Risk: High because durable workflow history, optimistic concurrency, and published-artifact integrity depend on this adapter.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyuHeroes.Gameplay.Core;
using HyuHeroes.Gameplay.Registries;

namespace HyuHeroes.Gameplay.Content.Workflow;

public sealed class FileContentRevisionRepository : IContentRevisionRepository
{
    private const int RepositoryFormatVersion = 1;
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _lockPath;
    private readonly GameplayRegistryCatalog _catalog;
    private readonly JsonSerializerOptions _jsonOptions;

    public FileContentRevisionRepository(
        string filePath,
        GameplayRegistryCatalog? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Repository file path cannot be empty.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        _lockPath = _filePath + ".lock";
        _catalog = catalog ?? GameplayRegistryCatalog.CreateSchemaV1();
        _jsonOptions = CreateJsonOptions();
    }

    public ContentWorkspaceRevision? Get(ContentRevisionKey key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        return WithRepositoryLock(() =>
        {
            var document = LoadDocument();
            var dto = document.Revisions.FirstOrDefault(item => Matches(item, key));
            return dto is null ? null : DeserializeRevision(dto);
        });
    }

    public ContentWorkspaceRevision? GetLatest(StableId workspaceId)
    {
        if (workspaceId == default) throw new ArgumentException("Workspace ID must be non-default.", nameof(workspaceId));
        return WithRepositoryLock(() =>
        {
            var dto = LoadDocument().Revisions
                .Where(item => item.WorkspaceId == workspaceId.Value)
                .OrderByDescending(item => item.Revision)
                .FirstOrDefault();
            return dto is null ? null : DeserializeRevision(dto);
        });
    }

    public IReadOnlyList<ContentWorkspaceRevision> List(StableId workspaceId)
    {
        if (workspaceId == default) throw new ArgumentException("Workspace ID must be non-default.", nameof(workspaceId));
        return WithRepositoryLock(() =>
            new ReadOnlyCollection<ContentWorkspaceRevision>(
                LoadDocument().Revisions
                    .Where(item => item.WorkspaceId == workspaceId.Value)
                    .OrderBy(item => item.Revision)
                    .Select(DeserializeRevision)
                    .ToArray()));
    }

    public IReadOnlyList<ContentWorkflowAuditEntry> GetAudit(ContentRevisionKey key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        return WithRepositoryLock(() =>
            new ReadOnlyCollection<ContentWorkflowAuditEntry>(
                LoadDocument().Audit
                    .Where(item => Matches(item, key))
                    .OrderBy(item => item.Sequence)
                    .Select(DeserializeAudit)
                    .ToArray()));
    }

    public ContentWorkspaceRevision Save(
        ContentWorkspaceRevision revision,
        long? expectedStoreVersion,
        ContentWorkflowAuditEntry auditEntry)
    {
        if (revision is null) throw new ArgumentNullException(nameof(revision));
        if (auditEntry is null) throw new ArgumentNullException(nameof(auditEntry));
        if (!revision.Key.Equals(auditEntry.Key))
        {
            throw new ArgumentException("Revision and audit entry must use the same key.", nameof(auditEntry));
        }

        return WithRepositoryLock(() =>
        {
            var document = LoadDocument();
            var existingIndex = document.Revisions.FindIndex(item => Matches(item, revision.Key));
            var currentVersion = existingIndex >= 0
                ? document.Revisions[existingIndex].StoreVersion
                : (long?)null;
            ValidateConcurrency(revision.Key, currentVersion, expectedStoreVersion);

            var nextStoreVersion = currentVersion is null
                ? 1
                : checked(currentVersion.Value + 1);
            var saved = revision.With(storeVersion: nextStoreVersion);
            var dto = SerializeRevision(saved);
            if (existingIndex >= 0)
            {
                document.Revisions[existingIndex] = dto;
            }
            else
            {
                document.Revisions.Add(dto);
            }

            var nextAuditSequence = document.Audit
                .Where(item => Matches(item, revision.Key))
                .Select(item => item.Sequence)
                .DefaultIfEmpty(0)
                .Max() + 1;
            document.Audit.Add(SerializeAudit(auditEntry.WithSequence(nextAuditSequence)));
            SaveDocument(document);
            return saved;
        });
    }

    private T WithRepositoryLock<T>(Func<T> action)
    {
        lock (_gate)
        {
            EnsureDirectory();
            try
            {
                using var repositoryLock = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return action();
            }
            catch (IOException exception)
            {
                throw new ContentConcurrencyException(
                    $"Content repository '{_filePath}' is locked or unavailable: {exception.Message}");
            }
        }
    }

    private RepositoryDocument LoadDocument()
    {
        if (!File.Exists(_filePath))
        {
            return new RepositoryDocument();
        }

        var json = File.ReadAllText(_filePath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RepositoryDocument();
        }

        var document = JsonSerializer.Deserialize<RepositoryDocument>(json, _jsonOptions)
            ?? throw new InvalidDataException("Content repository document deserialized to null.");
        if (document.RepositoryFormatVersion != RepositoryFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported content repository format '{document.RepositoryFormatVersion}'.");
        }

        ValidateDocument(document);
        return document;
    }

    private void SaveDocument(RepositoryDocument document)
    {
        document.RepositoryFormatVersion = RepositoryFormatVersion;
        document.Revisions = document.Revisions
            .OrderBy(item => item.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(item => item.Revision)
            .ToList();
        document.Audit = document.Audit
            .OrderBy(item => item.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(item => item.Revision)
            .ThenBy(item => item.Sequence)
            .ToList();

        var json = JsonSerializer.Serialize(document, _jsonOptions);
        var tempPath = _filePath + ".tmp";
        var backupPath = _filePath + ".bak";
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(_filePath))
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                File.Replace(tempPath, _filePath, backupPath);
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private ContentWorkspaceRevision DeserializeRevision(RevisionDto dto)
    {
        var key = new ContentRevisionKey(StableId.Parse(dto.WorkspaceId), dto.Revision);
        var package = dto.State == ContentWorkflowState.Published
            ? GameplayContentJsonLoader.Load(dto.PackageJson, _catalog)
            : GameplayContentJsonLoader.ParseAuthoringSnapshot(dto.PackageJson);
        var publication = dto.Publication is null
            ? null
            : new ContentPublicationRecord(
                dto.Publication.ContentVersion,
                dto.Publication.ContentHash,
                dto.Publication.PackagePath,
                dto.Publication.PublishedAt);

        return new ContentWorkspaceRevision(
            key,
            dto.State,
            package,
            dto.CreatedAt,
            dto.CreatedBy,
            dto.UpdatedAt,
            dto.UpdatedBy,
            dto.StoreVersion,
            publication);
    }

    private static RevisionDto SerializeRevision(ContentWorkspaceRevision revision) =>
        new()
        {
            WorkspaceId = revision.Key.WorkspaceId.Value,
            Revision = revision.Key.Revision,
            State = revision.State,
            StoreVersion = revision.StoreVersion,
            CreatedAt = revision.CreatedAt,
            CreatedBy = revision.CreatedBy,
            UpdatedAt = revision.UpdatedAt,
            UpdatedBy = revision.UpdatedBy,
            PackageJson = GameplayContentCanonicalWriter.Serialize(revision.Content),
            Publication = revision.Publication is null
                ? null
                : new PublicationDto
                {
                    ContentVersion = revision.Publication.ContentVersion,
                    ContentHash = revision.Publication.ContentHash,
                    PackagePath = revision.Publication.PackagePath,
                    PublishedAt = revision.Publication.PublishedAt
                }
        };

    private static ContentWorkflowAuditEntry DeserializeAudit(AuditDto dto) =>
        new(
            dto.Sequence,
            new ContentRevisionKey(StableId.Parse(dto.WorkspaceId), dto.Revision),
            dto.Action,
            dto.FromState,
            dto.ToState,
            dto.Actor,
            dto.OccurredAt,
            dto.Reason);

    private static AuditDto SerializeAudit(ContentWorkflowAuditEntry entry) =>
        new()
        {
            WorkspaceId = entry.Key.WorkspaceId.Value,
            Revision = entry.Key.Revision,
            Sequence = entry.Sequence,
            Action = entry.Action,
            FromState = entry.FromState,
            ToState = entry.ToState,
            Actor = entry.Actor,
            OccurredAt = entry.OccurredAt,
            Reason = entry.Reason
        };

    private static void ValidateConcurrency(
        ContentRevisionKey key,
        long? currentVersion,
        long? expectedStoreVersion)
    {
        if (currentVersion is null && expectedStoreVersion is null)
        {
            return;
        }

        if (currentVersion != expectedStoreVersion)
        {
            throw new ContentConcurrencyException(
                $"Revision '{key}' changed concurrently. Expected '{expectedStoreVersion?.ToString() ?? "<new>"}', actual '{currentVersion?.ToString() ?? "<missing>"}'.");
        }
    }

    private static void ValidateDocument(RepositoryDocument document)
    {
        if (document.Revisions.Any(item =>
            string.IsNullOrWhiteSpace(item.WorkspaceId) ||
            item.Revision <= 0 ||
            item.StoreVersion <= 0 ||
            string.IsNullOrWhiteSpace(item.PackageJson)))
        {
            throw new InvalidDataException("Content repository contains an invalid revision record.");
        }

        if (document.Revisions
            .GroupBy(item => $"{item.WorkspaceId}:{item.Revision}", StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Content repository contains duplicate revision keys.");
        }

        if (document.Audit.Any(item =>
            string.IsNullOrWhiteSpace(item.WorkspaceId) ||
            item.Revision <= 0 ||
            item.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(item.Action) ||
            string.IsNullOrWhiteSpace(item.Actor) ||
            string.IsNullOrWhiteSpace(item.Reason)))
        {
            throw new InvalidDataException("Content repository contains an invalid audit entry.");
        }
    }

    private static bool Matches(RevisionDto dto, ContentRevisionKey key) =>
        dto.WorkspaceId == key.WorkspaceId.Value && dto.Revision == key.Revision;

    private static bool Matches(AuditDto dto, ContentRevisionKey key) =>
        dto.WorkspaceId == key.WorkspaceId.Value && dto.Revision == key.Revision;

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class RepositoryDocument
    {
        public int RepositoryFormatVersion { get; set; } = FileContentRevisionRepository.RepositoryFormatVersion;
        public List<RevisionDto> Revisions { get; set; } = new();
        public List<AuditDto> Audit { get; set; } = new();
    }

    private sealed class RevisionDto
    {
        public string WorkspaceId { get; set; } = string.Empty;
        public int Revision { get; set; }
        public ContentWorkflowState State { get; set; }
        public long StoreVersion { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
        public string PackageJson { get; set; } = string.Empty;
        public PublicationDto? Publication { get; set; }
    }

    private sealed class PublicationDto
    {
        public string ContentVersion { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public string PackagePath { get; set; } = string.Empty;
        public DateTimeOffset PublishedAt { get; set; }
    }

    private sealed class AuditDto
    {
        public string WorkspaceId { get; set; } = string.Empty;
        public int Revision { get; set; }
        public long Sequence { get; set; }
        public string Action { get; set; } = string.Empty;
        public ContentWorkflowState? FromState { get; set; }
        public ContentWorkflowState ToState { get; set; }
        public string Actor { get; set; } = string.Empty;
        public DateTimeOffset OccurredAt { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
