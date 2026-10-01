/**
 * PUBLICATION_ARTIFACT_STORE
 * Purpose: Persists immutable signed packages before workflow publication and promotes the discovery manifest only after the revision commit.
 * Connections: PublicationWorker writes packages and manifests here; gameplay clients later consume the same package paths and manifest format.
 * Risk: High because overwrite/path bugs could make Published revisions point at missing or different runtime content.
 */
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyuHeroes.Gameplay.Content;

namespace HyuHeroes.AdminApi;

public interface IContentArtifactStore
{
    Task StorePackageAsync(
        string packagePath,
        string packageJson,
        string expectedContentHash,
        CancellationToken cancellationToken);

    Task PromoteManifestAsync(
        GameplayContentPackage package,
        string packagePath,
        bool makeDefault,
        CancellationToken cancellationToken);
}

public sealed class FileSystemContentArtifactStore : IContentArtifactStore
{
    private const string ManifestPath = "content/manifest.json";
    private readonly string _root;

    public FileSystemContentArtifactStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Artifact root cannot be empty.", nameof(root));
        }

        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task StorePackageAsync(
        string packagePath,
        string packageJson,
        string expectedContentHash,
        CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(packagePath);
        if (File.Exists(fullPath))
        {
            await VerifyExistingPackageAsync(
                fullPath,
                expectedContentHash,
                cancellationToken);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                packageJson,
                Encoding.UTF8,
                cancellationToken);
            try
            {
                File.Move(temporaryPath, fullPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                await VerifyExistingPackageAsync(
                    fullPath,
                    expectedContentHash,
                    cancellationToken);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task PromoteManifestAsync(
        GameplayContentPackage package,
        string packagePath,
        bool makeDefault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var presentation = package.Presentation
            ?? throw new InvalidDataException(
                "Published package requires presentation metadata.");
        var contentHash = package.ContentHash
            ?? throw new InvalidDataException("Published package is missing contentHash.");
        var manifestPath = ResolvePath(ManifestPath);
        var existing = File.Exists(manifestPath)
            ? await LoadManifestAsync(manifestPath, cancellationToken)
            : null;
        var entry = new ContentPackageManifestEntry(
            package.ContentVersion,
            contentHash,
            package.SchemaVersion,
            package.PublishedAt,
            packagePath,
            presentation.DefaultLocale,
            presentation.Localizations.Keys);
        var manifest = MergeManifest(existing, entry, makeDefault);
        await WriteManifestAtomicallyAsync(
            manifestPath,
            ContentPublisher.SerializeManifest(manifest),
            cancellationToken);
    }

    private static ContentPackageManifest MergeManifest(
        ContentPackageManifest? existing,
        ContentPackageManifestEntry entry,
        bool makeDefault)
    {
        if (existing is null)
        {
            return new ContentPackageManifest(entry.ContentVersion, new[] { entry });
        }

        var sameVersion = existing.Packages.FirstOrDefault(
            value => value.ContentVersion == entry.ContentVersion);
        if (sameVersion is not null && !sameVersion.IdentityEquals(entry))
        {
            throw new InvalidOperationException(
                $"Manifest content version '{entry.ContentVersion}' already points to a different artifact.");
        }

        var packages = sameVersion is null
            ? existing.Packages.Concat(new[] { entry })
            : existing.Packages;
        var defaultVersion = makeDefault
            ? entry.ContentVersion
            : existing.DefaultContentVersion;
        return new ContentPackageManifest(defaultVersion, packages);
    }

    private async Task VerifyExistingPackageAsync(
        string fullPath,
        string expectedContentHash,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var package = GameplayContentJsonLoader.Load(json);
        if (!string.Equals(
                package.ContentHash,
                expectedContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Immutable artifact '{fullPath}' already exists with a different contentHash.");
        }
    }

    private async Task<ContentPackageManifest> LoadManifestAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var dto = JsonSerializer.Deserialize<ManifestDto>(json)
            ?? throw new InvalidDataException("Manifest JSON is empty.");
        if (dto.ManifestVersion != ContentPackageManifest.CurrentManifestVersion)
        {
            throw new InvalidDataException(
                $"Unsupported manifest version '{dto.ManifestVersion}'.");
        }

        var entries = dto.Packages.Select(value =>
            new ContentPackageManifestEntry(
                value.ContentVersion,
                value.ContentHash,
                value.SchemaVersion,
                value.PublishedAt,
                value.PackagePath,
                value.DefaultLocale,
                value.Locales));
        return new ContentPackageManifest(
            dto.DefaultContentVersion,
            entries,
            dto.ManifestVersion);
    }

    private static async Task WriteManifestAtomicallyAsync(
        string fullPath,
        string json,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                Encoding.UTF8,
                cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Artifact path cannot be empty.", nameof(relativePath));
        }

        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                "Artifact path must be relative and cannot contain parent traversal.",
                nameof(relativePath));
        }

        var resolved = Path.GetFullPath(Path.Combine(_root, normalized));
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Artifact path resolves outside the configured root.",
                nameof(relativePath));
        }

        return resolved;
    }

    private sealed class ManifestDto
    {
        [JsonPropertyName("manifestVersion")]
        public int ManifestVersion { get; init; }

        [JsonPropertyName("defaultContentVersion")]
        public string DefaultContentVersion { get; init; } = string.Empty;

        [JsonPropertyName("packages")]
        public IReadOnlyList<ManifestEntryDto> Packages { get; init; } =
            Array.Empty<ManifestEntryDto>();
    }

    private sealed class ManifestEntryDto
    {
        [JsonPropertyName("contentVersion")]
        public string ContentVersion { get; init; } = string.Empty;

        [JsonPropertyName("contentHash")]
        public string ContentHash { get; init; } = string.Empty;

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("publishedAt")]
        public DateTimeOffset PublishedAt { get; init; }

        [JsonPropertyName("packagePath")]
        public string PackagePath { get; init; } = string.Empty;

        [JsonPropertyName("defaultLocale")]
        public string DefaultLocale { get; init; } = string.Empty;

        [JsonPropertyName("locales")]
        public IReadOnlyList<string> Locales { get; init; } = Array.Empty<string>();
    }
}
