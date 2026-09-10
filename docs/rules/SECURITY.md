# Security and Trust-Boundary Rules

## Current scope

The first playable version may be offline/local, but the architecture must not make future authoritative multiplayer unsafe or impractical.

## Trust model

The client is never a trusted source of authoritative gameplay truth once multiplayer/networked play exists.

Future server-authoritative flow should be:

```text
Client intent -> serialized Command -> server validation -> deterministic simulation -> authoritative result/events -> clients
```

Client-side previews, animations, and legality hints are convenience only.

## Input validation

Every command crossing a process/network/save boundary MUST be validated as untrusted input.

Validation includes:

- known command type/version;
- valid player identity/turn ownership;
- valid stable IDs;
- legal phase and target;
- legal resource cost;
- bounded numeric values;
- known referenced definitions;
- no duplicate/replayed command where sequence protection applies.

Never deserialize data and immediately mutate authoritative state without validation.

## Serialization safety

- Prefer explicit DTOs/schema over arbitrary object graph serialization.
- Persist stable IDs and primitive/domain values, not executable types or Unity object references.
- Version persisted/replay/network payloads.
- Reject unknown or malformed critical fields rather than guessing gameplay intent.
- Do not deserialize arbitrary type names supplied by untrusted input.

## Secrets and credentials

No secret belongs in source control.

Never commit API keys, OAuth secrets, access tokens, signing keys, production connection strings, private certificates, or service-account credentials.

Use environment variables/secret stores for external services and commit only sanitized examples/documentation.

## Economy and progression

If the game later introduces inventories, purchases, currencies, rewards, crafting, packs, rankings, or competitive progression, the server/backend MUST own authoritative balances and reward grants.

Do not trust client-provided currency balances, owned-card lists, match rewards, pack results, leaderboard scores, or purchase completion claims.

## RNG and fairness

Competitive gameplay RNG should be authoritative and auditable. Do not allow a client to submit chosen random outcomes.

If hidden information is involved, do not expose opponent hidden-zone contents or future RNG results merely for client convenience.

## Replay and logs

Diagnostic logs and replays should contain only data necessary for debugging/playback. Do not include access tokens or unrelated personal data.

If replays become shareable, explicitly decide which hidden information can be revealed and at what point.

## Denial-of-service and resolution loops

Trigger/effect systems must guard against accidental or malicious unbounded resolution. Use explicit resolution budgets/loop detection or another deterministic safeguard once mechanics can form cycles.

A safeguard MUST fail deterministically and visibly; it must not silently truncate a legitimate effect chain without a documented rule.

## External content and assets

Treat imported content, remote configuration, user-generated deck names, and future community content as untrusted input. Validate paths, IDs, sizes, formats, and text where applicable.

## Dependency policy

Add third-party packages only for a concrete need. Prefer maintained dependencies with clear licensing and avoid packages that require excessive permissions or introduce a large runtime surface for trivial functionality.

Security-sensitive dependency updates should be reviewable independently from unrelated gameplay changes where practical.

## Future authentication

When accounts are added, use established identity/authentication providers or well-reviewed backend frameworks rather than implementing password cryptography/session security from scratch.

Authentication proves account identity; it does not make client gameplay data trustworthy.
