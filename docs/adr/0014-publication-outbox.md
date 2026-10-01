# ADR 0014: Durable asynchronous publication outbox

## Status

Accepted for the content publication pipeline.

## Context

The synchronous Admin API publication path could mark a revision Published immediately after generating package JSON, before a runtime artifact store had durably persisted that package. A process crash or storage failure could therefore leave PostgreSQL claiming Published while the package path was missing.

Publication crosses two durability domains:

- authoring state in PostgreSQL;
- runtime package and manifest storage.

A distributed transaction across PostgreSQL and arbitrary artifact storage is not assumed.

## Decision

Publication uses a durable PostgreSQL outbox and an idempotent artifact store.

### HTTP contract

`POST /api/content/workspaces/{workspaceId}/revisions/{revision}/publish` no longer performs the final Published transition synchronously.

The endpoint:

1. validates Publisher RBAC;
2. verifies the revision is Approved and the supplied `storeVersion` is current;
3. prepares and signs the canonical package;
4. persists a publication outbox job;
5. returns HTTP `202 Accepted`.

The response contains the job snapshot and a `Location` header pointing at:

`GET /api/content/publications/{jobId}`

Clients poll that endpoint until the stage becomes `Completed` or `Failed`.

The client does not submit or own the current manifest. Manifest history is managed server-side by the artifact store.

### Durable stages

The outbox stages are:

1. `Queued`;
2. `PackageStored`;
3. `RevisionCommitted`;
4. `Completed`.

`Failed` is a terminal stage.

A worker leases one stage at a time. PostgreSQL lease metadata permits recovery after process termination. Expired leases are eligible for retry.

### Safety ordering

The worker always performs:

```text
signed package
    ↓
store immutable package
    ↓
Approved -> Published
    ↓
promote manifest
    ↓
complete outbox
```

This ordering provides the required invariant:

> A revision may enter Published only after the exact package identified by its `contentHash` is durably present at its package path.

If package storage fails, the revision remains Approved.

If the revision commit fails after package storage, an unreferenced immutable package may remain, but no manifest promotion occurs.

If manifest promotion fails after the revision commit, the package is already durable. The worker retries manifest promotion without republishing or rewriting the package.

### Artifact immutability

Writing a package path is idempotent only when the existing package has the same verified `contentHash`.

If the same path already contains a different hash, publication fails rather than overwriting it.

Artifact paths are restricted to relative paths beneath `HYU_CONTENT_ARTIFACT_ROOT`.

### Manifest promotion

The worker reads the durable manifest, merges the new package entry, preserves prior versions, and writes the manifest atomically.

Publishing the same content version with different identity metadata is rejected.

Only one outbox lease is active globally at a time in the prototype implementation. This intentionally serializes manifest promotion and removes cross-worker manifest races. Publication volume is expected to be low enough that correctness is preferred over throughput.

### Idempotent recovery

Each stage is safe to replay:

- package write: same path and same hash is accepted;
- revision commit: an already Published revision is accepted only when version/hash/path match the job;
- manifest promotion: an identical version entry is accepted.

The signed package timestamp is read from the package itself when committing the revision. PostgreSQL `timestamptz` precision is not used as part of package identity.

## Consequences

- the Admin Dashboard receives asynchronous publication jobs instead of synchronous package/manifest payloads;
- PostgreSQL can never report Published before the package exists;
- crashes between stages are recoverable from persisted outbox state;
- manifest failures cannot make the package itself unavailable;
- artifact storage can later be replaced by S3, R2, Azure Blob, or another implementation of `IContentArtifactStore`;
- orphan immutable packages are possible after a failed revision commit, but they are never promoted into the runtime manifest and can be garbage-collected separately.
