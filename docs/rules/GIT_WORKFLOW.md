# Git Workflow Rules

## Branching

Use short-lived task branches for substantive work. Recommended pattern:

`type/short-description`

Examples:

- `feat/command-validation`
- `feat/card-effects`
- `fix/trigger-ordering`
- `refactor/state-ownership`
- `docs/gameplay-rules`

Do not mix unrelated tasks in one branch.

## Commits

Commits SHOULD be small, coherent, and independently understandable. Prefer conventional-style prefixes:

- `feat:`
- `fix:`
- `refactor:`
- `test:`
- `docs:`
- `chore:`

A commit should describe what changed, not vague activity such as `update files`.

## Pull requests

A PR SHOULD include:

- behavior/architecture summary;
- important implementation decisions;
- tests run;
- gameplay rule documents changed, if any;
- determinism/replay implications;
- known risks or follow-up work.

Do not approve/merge a change merely because it visually works in Unity. Gameplay changes require simulation-level test evidence.

## Unity version control rules

The repository MUST track:

- `Assets/` content intended for the project;
- all corresponding Unity `.meta` files;
- `Packages/manifest.json`;
- `Packages/packages-lock.json` when generated;
- `ProjectSettings/`.

The repository MUST NOT intentionally track generated Unity directories such as `Library`, `Temp`, `Obj`, `Logs`, `UserSettings`, or local build output.

Unity `.meta` files MUST NOT be globally ignored or casually regenerated. They carry asset GUID identity.

Use Unity Asset Serialization `Force Text` and Version Control `Visible Meta Files` for collaborative/version-controlled development.

## Git LFS

Large binary source assets SHOULD use Git LFS when they would materially bloat repository history, especially high-resolution PSD/PSB, large audio/video, and large model source files.

Do not automatically put every small PNG/icon into LFS without a repository-size reason.

## Generated files

Generated IDE/project files such as `.csproj`, `.sln`, caches, local editor configuration, build artifacts, and logs SHOULD be ignored unless a later toolchain explicitly requires tracking a specific generated file.

## Secrets

Never commit:

- `.env` with real values;
- API keys;
- access tokens;
- private certificates;
- signing secrets;
- service credentials.

Provide sanitized `.env.example` or documented configuration keys where needed.

## Rule/document changes

A gameplay or architecture implementation that invalidates an existing rule MUST update the relevant file in `docs/rules/` in the same branch/PR.

Do not modify rules retroactively just to make noncompliant code appear compliant.

## Merge discipline

Before merge:

1. compile/build relevant target;
2. run relevant tests;
3. inspect diff for generated files and accidental binary additions;
4. verify no secrets are present;
5. verify Unity `.meta` changes correspond to intentional asset changes;
6. verify architectural dependency direction remains valid.

## Repository history

Do not rewrite shared branch history or force-push protected/shared branches unless explicitly required and understood. Avoid large-format churn and mass file moves in the same change as gameplay modifications unless necessary.
