# ADR 0008: Canonical content package and deterministic content hash

## Status

Accepted for schema v1.

## Context

Gameplay definitions are now executable by the authoritative C# kernel, but the simulator and future admin/server clients still need one immutable published artifact that identifies the exact rules content used by a match.

A package must be portable across processes and presentation clients, while replay/server compatibility requires a deterministic identity that does not depend on dictionary insertion order, serializer reflection order, whitespace, or the host runtime.

## Decision

Published gameplay content is distributed as a GameplayContentPackage containing:

- schemaVersion;
- contentVersion;
- publishedAt;
- a registry-ID snapshot;
- top-level published ability definitions;
- top-level published card definitions;
- contentHash.

The runtime JSON loader is strict and reconstructs the existing typed schema objects rather than introducing a second gameplay model.

### Canonicalization

GameplayContentCanonicalWriter writes properties explicitly. Canonical hash input uses compact UTF-8 JSON and omits contentHash itself.

The writer:

- sorts registry group names ordinally;
- sorts every registry ID set;
- sorts top-level abilities and cards by stable ID;
- sorts parameter-map keys and stat-map keys;
- keeps semantically ordered arrays in authored order, including ability bindings, effect sequences, formula arguments, condition children, selector filters, and nested effect lists;
- emits enum names and StableIds as stable strings;
- normalizes publishedAt to UTC round-trip format;
- emits decimal values as JSON numbers.

contentHash is:

    sha256:<lowercase hexadecimal SHA-256 of canonical UTF-8 JSON without contentHash>

A change to canonicalization rules is a content/replay compatibility change and requires a schema/package-version decision.

### Loader trust boundary

GameplayContentJsonLoader:

1. parses strict JSON;
2. rejects unknown properties in typed schema objects;
3. reconstructs typed schema-v1 definitions;
4. recomputes and compares contentHash;
5. verifies the package registry snapshot against GameplayRegistryCatalog;
6. runs GameplaySchemaValidator over every card and ability;
7. requires published status;
8. verifies referenced top-level ability IDs exist.

The loader returns typed gameplay objects only after every gate succeeds.

### Registry snapshot

The package snapshots IDs for alignments, classes, factions, tags, keywords, stats, resources, phases, formula variables, triggers, conditions, durations, usage limits, formula operators, and effects.

The current schema-v1 loader requires an exact match with the authoritative runtime registry catalog. Registry migration/version negotiation is deferred until multiple schema/catalog versions need to coexist.

### Presentation clients

Presentation clients may read the same JSON package for names, stats, classification, ability descriptions, and fixture setup, but they do not become authoritative rules engines. The C# loader/validator remains the executable trust boundary.

## Consequences

- a replay or server session can pin one exact content hash;
- client/server content mismatch can be detected before simulation;
- package files may be pretty-printed without changing semantic hash identity;
- dictionary/set insertion order cannot change the hash;
- malformed or tampered packages fail before runtime execution;
- the HTML simulator can remove duplicated card-definition fixtures and consume the published package directly;
- future admin publishing can produce the same format instead of inventing a second transport schema.
