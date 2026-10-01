# ADR 0011: Durable file-backed content revision repository

## Status

Accepted for the prototype Admin Dashboard/backend.

## Context

ADR 0010 introduced the repository contract and workflow semantics, but the in-memory adapter loses revision history on process restart. The prototype needs a durable persistence adapter now, while production is still expected to move to PostgreSQL.

## Decision

`FileContentRevisionRepository` implements `IContentRevisionRepository` using one JSON document containing revision records and audit entries.

Each revision stores:

- workspace ID and workspace revision;
- workflow state;
- optimistic `storeVersion`;
- created/updated actor and UTC timestamps;
- canonical package snapshot JSON;
- optional publication metadata.

Audit entries are stored in the same document and retain their per-revision monotonically increasing sequence.

### Draft package codec

Draft/Review/Approved snapshots may be incomplete or not yet publishable. They therefore use `GameplayContentJsonLoader.ParseAuthoringSnapshot`, an internal strict structural parser that does not verify contentHash and does not mark the package trusted.

The existing public `GameplayContentJsonLoader.Load` behavior is unchanged. Published revision reloads continue to use that trusted path and therefore re-verify contentHash, schema, registry compatibility, publication status, references, and presentation coverage.

### Concurrency

Every save reloads the current file before applying optimistic-concurrency checks. A caller must supply the expected `storeVersion`; mismatches raise `ContentConcurrencyException`.

The adapter also uses a sidecar exclusive lock file while reading or writing through a repository instance. Revision update and audit append are serialized into one document write.

### Write strategy

Repository writes are staged into a temporary file. Existing repository documents are replaced using a backup-assisted file replace; new repositories are created by moving the staged file into place.

The repository format has an explicit integer `repositoryFormatVersion`. Unsupported versions fail rather than being guessed or silently migrated.

### Scope

This adapter is for local development, simulator tooling, and a single-node prototype Admin Dashboard/backend. It is not the final multi-instance production store.

A PostgreSQL adapter should preserve the same repository contract while mapping optimistic concurrency to row versions/transactions and audit append to durable relational records.

## Consequences

- content revisions and audit history survive process restarts;
- Draft content can be persisted without pretending it is a valid published package;
- Published snapshots are re-verified when reloaded;
- independent repository instances observe stale `storeVersion` conflicts instead of silently overwriting changes;
- the Admin Dashboard can use durable storage immediately without depending on a cloud database;
- production migration to PostgreSQL does not require changing `ContentWorkflowService`.
