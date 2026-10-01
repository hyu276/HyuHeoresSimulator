# ADR 0009: Published manifests, version pins, and presentation localization

## Status

Accepted for the prototype content pipeline.

## Context

ADR 0008 established deterministic signed gameplay packages. A production-facing authoring workflow also needs immutable publication history, discovery of the current package, explicit replay/client pinning, and localization/presentation data without moving UI concerns into gameplay rules.

## Decision

### Publisher boundary

`ContentPublisher` is the only kernel-level publication boundary. It validates a package, requires presentation/localization metadata, signs the package, serializes it, and returns an updated manifest artifact. Persistence to Git, object storage, a database, or a CDN remains an adapter concern outside the gameplay kernel.

Published `contentVersion` values are immutable. Republishing an existing version with a different hash, path, schema version, locale set, or presentation artifact is rejected. Any content change requires a new version.

### Presentation snapshot

`GameplayContentPackage.presentation` contains:

- `defaultLocale`;
- localization bundles keyed by locale and `localizationKey`;
- definition-level presentation metadata such as artwork reference, icon reference, and frame style.

Presentation data participates in canonical serialization and therefore in `contentHash`. Gameplay evaluation does not read presentation metadata.

The default locale must contain a localization entry for every top-level card and ability. Presentation metadata may reference only definitions contained in the package.

### Manifest

`content/manifest.json` uses manifest version 1 and contains a default content version plus immutable package entries. Each entry records:

- `contentVersion`;
- exact `contentHash`;
- `schemaVersion`;
- `publishedAt`;
- relative `packagePath`;
- `defaultLocale`;
- published locale list.

Servers, replays, tests, and clients may resolve a package by exact `(contentVersion, contentHash)`. The default version is convenience discovery only and is not a substitute for a replay/server pin.

### Browser simulator

The HTML simulator loads the manifest first. With no explicit pin it uses `defaultContentVersion`. When URL parameters `contentVersion` and `contentHash` are supplied, both are required and the pair must exist exactly in the manifest.

The browser then verifies package version/hash/schema fields against the manifest entry before using presentation data. Cryptographic canonical hash verification remains authoritative in the C# loader/publisher; JavaScript does not become a second gameplay trust implementation.

`locale` may be supplied as a URL parameter. Otherwise the client attempts browser locales and falls back to the manifest/package default locale.

## Consequences

- old published packages remain immutable instead of being overwritten;
- a match/replay can identify one exact content artifact;
- switching the manifest default does not alter historical pins;
- localization and artwork/frame metadata use the same signed artifact as gameplay definitions;
- the HTML simulator displays localized names/descriptions while gameplay mechanics still come from the same card definitions;
- future Admin Dashboard publishing can persist `PublishedContentArtifact.PackageJson` and `ManifestJson` through storage adapters.
