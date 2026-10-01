/**
 * CONTENT_PUBLISHER
 * Purpose: Converts validated published definitions into signed immutable package artifacts and a version-pinned discovery manifest.
 * Connections: Uses GameplayContentPackageValidator and GameplayContentCanonicalWriter; admin/backend adapters persist the returned JSON artifacts.
 * Risk: High because publishing establishes immutable content-version/hash identities consumed by servers, replays, and presentation clients.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HyuHeroes.Gameplay.Registries;

namespace HyuHeroes.Gameplay.Content;

public sealed class ContentPackageManifestEntry
{
    public ContentPackageManifestEntry(
        string contentVersion,
        string contentHash,
        int schemaVersion,
        DateTimeOffset publishedAt,
        string packagePath,
        string defaultLocale,
        IEnumerable<string> locales)
    {
        ContentVersion = RequireText(contentVersion, nameof(contentVersion));
        ContentHash = RequireHash(contentHash);
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));

        SchemaVersion = schemaVersion;
        PublishedAt = publishedAt.ToUniversalTime();
        PackagePath = NormalizePackagePath(packagePath);
        DefaultLocale = LocalizationBundle.NormalizeLocale(defaultLocale);

        var localeValues = (locales ?? throw new ArgumentNullException(nameof(locales)))
            .Select(LocalizationBundle.NormalizeLocale)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (localeValues.Length == 0)
        {
            throw new ArgumentException("Manifest entry must publish at least one locale.", nameof(locales));
        }

        if (!localeValues.Contains(DefaultLocale, StringComparer.Ordinal))
        {
            throw new ArgumentException("Default locale must be included in manifest locales.", nameof(locales));
        }

        Locales = new ReadOnlyCollection<string>(localeValues);
    }

    public string ContentVersion { get; }
    public string ContentHash { get; }
    public int SchemaVersion { get; }
    public DateTimeOffset PublishedAt { get; }
    public string PackagePath { get; }
    public string DefaultLocale { get; }
    public IReadOnlyList<string> Locales { get; }

    public bool IdentityEquals(ContentPackageManifestEntry other) =>
        other is not null &&
        ContentVersion == other.ContentVersion &&
        ContentHash == other.ContentHash &&
        SchemaVersion == other.SchemaVersion &&
        PackagePath == other.PackagePath &&
        DefaultLocale == other.DefaultLocale &&
        Locales.SequenceEqual(other.Locales);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();

    private static string RequireHash(string contentHash)
    {
        var value = RequireText(contentHash, nameof(contentHash));
        if (value.Length != 71 ||
            !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.Skip(7).Any(character =>
                !(character >= '0' && character <= '9') &&
                !(character >= 'a' && character <= 'f')))
        {
            throw new ArgumentException("Content hash must be sha256: followed by 64 lowercase hexadecimal characters.", nameof(contentHash));
        }

        return value;
    }

    private static string NormalizePackagePath(string packagePath)
    {
        var value = RequireText(packagePath, nameof(packagePath)).Replace('\\', '/');
        if (value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains("../", StringComparison.Ordinal) ||
            value.Equals("..", StringComparison.Ordinal) ||
            !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Package path must be a relative JSON path without parent traversal.",
                nameof(packagePath));
        }

        return value;
    }
}

public sealed class ContentPackageManifest
{
    public const int CurrentManifestVersion = 1;

    public ContentPackageManifest(
        string defaultContentVersion,
        IEnumerable<ContentPackageManifestEntry> packages,
        int manifestVersion = CurrentManifestVersion)
    {
        if (manifestVersion != CurrentManifestVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(manifestVersion),
                $"Manifest version must be {CurrentManifestVersion}.");
        }

        if (string.IsNullOrWhiteSpace(defaultContentVersion))
        {
            throw new ArgumentException("Default content version cannot be empty.", nameof(defaultContentVersion));
        }

        var values = (packages ?? throw new ArgumentNullException(nameof(packages))).ToArray();
        if (values.Any(value => value is null))
        {
            throw new ArgumentException("Manifest packages cannot contain null values.", nameof(packages));
        }

        if (values.GroupBy(value => value.ContentVersion, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Manifest content versions must be unique.", nameof(packages));
        }

        ManifestVersion = manifestVersion;
        DefaultContentVersion = defaultContentVersion.Trim();
        Packages = new ReadOnlyCollection<ContentPackageManifestEntry>(
            values.OrderBy(value => value.ContentVersion, StringComparer.Ordinal).ToArray());

        if (!Packages.Any(value => value.ContentVersion == DefaultContentVersion))
        {
            throw new ArgumentException(
                $"Default content version '{DefaultContentVersion}' is not present in manifest packages.",
                nameof(defaultContentVersion));
        }
    }

    public int ManifestVersion { get; }
    public string DefaultContentVersion { get; }
    public IReadOnlyList<ContentPackageManifestEntry> Packages { get; }

    public ContentPackageManifestEntry ResolvePinned(
        string contentVersion,
        string contentHash)
    {
        var entry = Packages.FirstOrDefault(value =>
            value.ContentVersion == contentVersion &&
            value.ContentHash == contentHash);
        return entry
            ?? throw new InvalidDataException(
                $"Manifest does not contain pinned content '{contentVersion}' with hash '{contentHash}'.");
    }

    public ContentPackageManifestEntry ResolveDefault() =>
        Packages.Single(value => value.ContentVersion == DefaultContentVersion);
}

public sealed class PublishedContentArtifact
{
    public PublishedContentArtifact(
        GameplayContentPackage package,
        string packageJson,
        ContentPackageManifest manifest,
        string manifestJson)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        PackageJson = packageJson ?? throw new ArgumentNullException(nameof(packageJson));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ManifestJson = manifestJson ?? throw new ArgumentNullException(nameof(manifestJson));
    }

    public GameplayContentPackage Package { get; }
    public string PackageJson { get; }
    public ContentPackageManifest Manifest { get; }
    public string ManifestJson { get; }
}

public static class ContentPublisher
{
    public static PublishedContentArtifact Publish(
        GameplayContentPackage package,
        string packagePath,
        ContentPackageManifest? existingManifest = null,
        bool makeDefault = true,
        GameplayRegistryCatalog? catalog = null)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        var presentation = package.Presentation
            ?? throw new InvalidDataException(
                "Published package artifacts require localization/presentation metadata.");

        var runtimeCatalog = catalog ?? GameplayRegistryCatalog.CreateSchemaV1();
        GameplayContentPackageValidator.Validate(package, runtimeCatalog);

        var signedPackage = GameplayContentCanonicalWriter.Sign(package);
        var hash = signedPackage.ContentHash
            ?? throw new InvalidOperationException("Signed package did not produce a content hash.");
        var entry = new ContentPackageManifestEntry(
            signedPackage.ContentVersion,
            hash,
            signedPackage.SchemaVersion,
            signedPackage.PublishedAt,
            packagePath,
            presentation.DefaultLocale,
            presentation.Localizations.Keys);

        var manifest = UpsertManifest(existingManifest, entry, makeDefault);
        return new PublishedContentArtifact(
            signedPackage,
            GameplayContentCanonicalWriter.Serialize(signedPackage),
            manifest,
            SerializeManifest(manifest));
    }

    public static string SerializeManifest(ContentPackageManifest manifest, bool indented = true)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = indented, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("manifestVersion", manifest.ManifestVersion);
            writer.WriteString("defaultContentVersion", manifest.DefaultContentVersion);
            writer.WritePropertyName("packages");
            writer.WriteStartArray();
            foreach (var entry in manifest.Packages)
            {
                writer.WriteStartObject();
                writer.WriteString("contentVersion", entry.ContentVersion);
                writer.WriteString("contentHash", entry.ContentHash);
                writer.WriteNumber("schemaVersion", entry.SchemaVersion);
                writer.WriteString("publishedAt", entry.PublishedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("packagePath", entry.PackagePath);
                writer.WriteString("defaultLocale", entry.DefaultLocale);
                writer.WritePropertyName("locales");
                writer.WriteStartArray();
                foreach (var locale in entry.Locales)
                {
                    writer.WriteStringValue(locale);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ContentPackageManifest UpsertManifest(
        ContentPackageManifest? existingManifest,
        ContentPackageManifestEntry entry,
        bool makeDefault)
    {
        if (existingManifest is null)
        {
            return new ContentPackageManifest(entry.ContentVersion, new[] { entry });
        }

        var existingEntry = existingManifest.Packages
            .FirstOrDefault(value => value.ContentVersion == entry.ContentVersion);
        if (existingEntry is not null && !existingEntry.IdentityEquals(entry))
        {
            throw new InvalidOperationException(
                $"Published content version '{entry.ContentVersion}' is immutable and already points to a different artifact.");
        }

        var packages = existingEntry is null
            ? existingManifest.Packages.Concat(new[] { entry })
            : existingManifest.Packages;
        var defaultVersion = makeDefault
            ? entry.ContentVersion
            : existingManifest.DefaultContentVersion;
        return new ContentPackageManifest(defaultVersion, packages);
    }
}
