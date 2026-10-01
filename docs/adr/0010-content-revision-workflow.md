# ADR 0010: Content revision workflow and repository boundary

## Status

Accepted for the prototype authoring pipeline.

## Context

Published gameplay packages are immutable and hash-pinned, but creative and balance teams need a mutable authoring lifecycle before publication. Editing published JSON directly would bypass review, audit history, optimistic concurrency, and the ContentPublisher trust boundary.

The workflow must also permit a published release to remain immutable while the next release is edited independently.

## Decision

Authoring is organized as workspace revisions keyed by:

workspace StableId + positive revision number.

A workspace revision contains a complete typed GameplayContentPackage candidate plus workflow metadata. The gameplay package remains the common schema; no second card/effect model is introduced for the Admin Dashboard.

### Workflow states

The allowed primary path is:

    Draft -> Review -> Approved -> Published

Review or Approved revisions may return to Draft when changes are requested. Published revisions are terminal and immutable. A new edit cycle creates the next workspace revision.

The workflow state is stored separately from the immutable revision identity. During each transition, definition headers are materialized to the corresponding schema ContentStatus. Publishing materializes every top-level and inline definition as Published before calling ContentPublisher.

### Validation gates

Draft saves may be incomplete and are not forced through publication validation.

Moving Draft to Review runs GameplayContentPackageValidator.ValidateForAuthoring, which verifies:

- supported schema version;
- authoritative registry snapshot;
- card and ability schema validity;
- cross-package ability references;
- presentation metadata references;
- default-locale coverage.

Unlike publication validation, authoring validation does not require definitions to already have Published status.

Approval validates again. Publication then uses the stricter existing ContentPublisher, which requires published definitions, signs the package, and emits manifest artifacts.

### Repository contract

IContentRevisionRepository is the persistence boundary used by ContentWorkflowService.

It provides:

- revision lookup;
- latest-revision lookup;
- revision history listing;
- audit history;
- optimistic-concurrency save.

Every save requires the caller's expected storeVersion. The repository increments the version atomically. A stale version fails with ContentConcurrencyException instead of silently overwriting another editor.

The in-memory implementation is the reference semantics and test adapter. Durable adapters must preserve the same save + audit atomicity contract.

### Audit trail

Every mutation appends an immutable audit entry containing:

- workspace revision;
- action;
- previous state;
- resulting state;
- actor;
- UTC timestamp;
- reason;
- monotonically increasing sequence within the revision.

Publication metadata additionally records content version, exact content hash, package path, and publication timestamp.

### Publication atomicity

ContentWorkflowService.Publish calls ContentPublisher before changing repository state to Published. If validation, signing, or manifest creation fails, the Approved revision remains unchanged.

A repository save occurs only after a complete PublishedContentArtifact exists.

## Consequences

- published releases cannot be edited in place;
- new release work starts at workspace revision N+1;
- Admin Dashboard clients can use optimistic concurrency and receive explicit conflict errors;
- review/approval/publish actions are auditable;
- draft content can remain incomplete while Review and Publish have progressively stronger validation gates;
- persistence implementation can move from prototype storage to PostgreSQL without changing workflow rules;
- the next adapter can persist revision records and audit history durably while reusing this exact service contract.
