# ADR 0013: ASP.NET Core Admin API and RBAC

## Status

Accepted for the prototype content-management backend.

## Context

The content workflow and PostgreSQL repository provide durable Draft, Review, Approved, and Published revision semantics, but the Admin Dashboard requires an authenticated HTTP boundary. Browser clients must not call PostgreSQL directly or reproduce workflow transition rules.

The API must preserve optimistic concurrency, authenticated audit identity, and separation between authorship, review, and publication privileges.

## Decision

A separate `HyuHeroes.AdminApi` ASP.NET Core 8 application exposes the existing content workflow over HTTP.

The API depends on:

- `HyuHeroes.Gameplay` for workflow/domain semantics;
- `HyuHeroes.ContentStore.Postgres` for durable persistence;
- ASP.NET Core JWT bearer authentication for identity and role claims.

The gameplay assembly remains HTTP- and authentication-agnostic.

### Authentication

Admin API callers authenticate with JWT bearer tokens.

Required runtime configuration:

- `HYU_ADMIN_JWT_ISSUER`;
- `HYU_ADMIN_JWT_AUDIENCE`;
- `HYU_ADMIN_JWT_SIGNING_KEY`;
- `HYU_CONTENT_POSTGRES`.

The prototype uses a symmetric signing key and requires at least 32 UTF-8 bytes. A later production deployment may replace token issuance with an external OIDC provider without changing endpoint authorization policies or workflow semantics.

The JWT `sub` claim is the authoritative audit actor. Role membership is read from the standard JWT `role` claim.

### Role policies

Roles are independent and may be combined on one identity:

- authenticated caller: read revisions and audit history;
- `Author`: create/update Draft revisions and submit for Review;
- `Reviewer`: return Review/Approved revisions to Draft and approve Review revisions;
- `Publisher`: publish Approved revisions.

Possessing one role does not implicitly grant another role. An administrator requiring multiple capabilities receives multiple role claims.

### HTTP workflow

The API exposes workspace revision endpoints for:

- list revision history;
- get a complete revision snapshot;
- get audit history;
- create Draft;
- update Draft;
- submit Draft for Review;
- return Review/Approved to Draft;
- approve Review;
- publish Approved.

Mutation requests carry the current `storeVersion`. The API passes that token unchanged to `ContentWorkflowService`. Stale writes return HTTP 409 rather than overwriting concurrent edits.

Authoring package bodies are parsed by the shared strict authoring snapshot parser. The API does not define a second gameplay model.

### Error boundary

The HTTP layer maps domain/storage failures without changing semantics:

- malformed IDs, JSON, or authoring data -> 400;
- stale `storeVersion` -> 409;
- invalid workflow transition -> 409;
- missing read resource -> 404;
- unauthenticated caller -> 401;
- authenticated caller missing the required role -> 403.

### Publication

Publish delegates to `ContentWorkflowService.Publish` and returns:

- persisted Published revision;
- canonical package JSON;
- manifest JSON.

Artifact persistence to Git, object storage, or CDN remains a separate deployment adapter. The HTTP API does not mutate runtime content files directly.

### Integration testing

`HyuHeroes.AdminApi.Tests` boots the real API using `WebApplicationFactory` against the PostgreSQL service provided by GitHub Actions.

The HTTP suite verifies:

- anonymous requests receive 401;
- authenticated users with the wrong role receive 403;
- Author -> Reviewer -> Publisher transitions execute through real HTTP/JWT/PostgreSQL boundaries;
- audit actors come from JWT subjects;
- stale `storeVersion` updates return 409 and do not overwrite the current draft;
- publication returns canonical package and manifest artifacts.

The owner merge gate runs this suite before auto-approval and squash merge.

## Consequences

- the Admin Dashboard remains an untrusted presentation client;
- PostgreSQL credentials remain server-side;
- workflow, validation, content hashing, and publication rules are not duplicated in TypeScript;
- audit history records authenticated JWT subjects instead of browser-supplied actor names;
- concurrent editors receive explicit conflicts through `storeVersion`;
- review and publication permissions are independently assignable;
- future OIDC/JWT issuer replacement does not require changing gameplay workflow code.
