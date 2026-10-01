# ADR 0011: PostgreSQL content revision persistence

## Status

Accepted for the admin/content-management persistence boundary.

## Context

ADR 0010 defined the revision workflow and `IContentRevisionRepository`, but its reference implementation is process-local memory. Admin Dashboard usage requires durable storage that preserves revision history, optimistic concurrency, audit sequencing, and publication metadata across process restarts.

The gameplay kernel must remain database-agnostic and Unity-compatible. A PostgreSQL driver must therefore not become a dependency of `HyuHeroes.Gameplay`.

## Decision

A separate `HyuHeroes.ContentStore.Postgres` .NET 8 adapter implements `IContentRevisionRepository`.

The adapter uses PostgreSQL for two tables:

- `content_revisions`, keyed by `(workspace_id, revision)`;
- `content_revision_audit`, keyed by `(workspace_id, revision, sequence)` and protected by a foreign key to the revision.

Each revision row stores the complete typed gameplay package candidate as `jsonb`, plus workflow metadata, `store_version`, and nullable publication metadata.

### Transaction and concurrency contract

`Save` opens one database transaction and locks the revision row with `SELECT ... FOR UPDATE`.

For an existing revision, the caller must provide the exact current `store_version`. Missing or stale versions fail with `ContentConcurrencyException`.

For a new revision, `expectedStoreVersion` must be null.

Within the same transaction the repository:

1. validates optimistic concurrency;
2. increments `store_version`;
3. inserts or updates the revision row;
4. calculates the next audit sequence while the revision lock is held;
5. inserts the audit entry;
6. commits.

Revision mutation and audit append therefore cannot commit independently.

### Authoring snapshot serialization

Draft, Review, and Approved packages do not have a publication `contentHash` and may intentionally be semantically incomplete while still being structurally valid typed schema objects.

`GameplayContentCanonicalWriter.Serialize` is reused as the storage representation. `GameplayContentJsonLoader.LoadAuthoringSnapshot` reconstructs strict typed packages without forcing publication validation. If a stored snapshot already carries a `contentHash`, it is still recomputed and verified.

Workflow validation remains in `ContentWorkflowService`:

- Draft saves may be incomplete;
- Review and Approval run authoring validation;
- Publish uses the strict `ContentPublisher` boundary.

### Database driver boundary

`Npgsql` is referenced only by the PostgreSQL adapter project. The gameplay assembly does not acquire a PostgreSQL dependency.

The adapter exposes `EnsureSchema` for prototype/bootstrap use. Production deployments may execute the same schema through normal migration infrastructure before starting the admin backend.

### Integration testing

The repository test suite always compiles and validates serialization/schema contracts.

When `HYU_TEST_POSTGRES` is supplied, the same test suite executes the complete Draft -> Review -> Approved -> Published workflow against a real PostgreSQL database, verifies persisted publication metadata and audit ordering, and checks stale-version rejection.

## Consequences

- authoring revisions survive process restarts;
- multiple Admin Dashboard/backend instances can use database-level row locking and optimistic concurrency;
- audit history is persisted atomically with revision mutations;
- gameplay/Unity code remains independent from database drivers;
- PostgreSQL can later be wrapped by an ASP.NET Core admin API without changing workflow semantics;
- published gameplay packages remain immutable runtime artifacts and are not queried from mutable authoring tables during matches.
