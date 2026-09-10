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

## Automatic owner PR squash-merge policy

The repository uses `.github/workflows/auto-approve-squash-merge.yml` to automate the final merge path for trusted owner-authored pull requests.

Automatic merge is allowed only when all of the following are true:

- the PR is authored by `hyu276`;
- the PR originates from a branch in this same repository, not a fork;
- the PR targets `main`;
- the PR is not a draft;
- the repository quality gate has completed successfully;
- the exact head SHA that passed the quality gate is still the PR head SHA at merge time.

Eligible PRs MUST be merged using **squash** semantics. The automation MUST NOT use admin/bypass merge to defeat branch protection or required checks.

The workflow MAY submit an approval review using the GitHub Actions bot. GitHub repository settings must permit Actions to create and approve pull requests for this review to succeed. If GitHub refuses the approval review, the workflow may still merge only when the repository's active branch/ruleset requirements otherwise permit it.

PRs from other authors, forks, different base branches, or draft PRs MUST NOT be automatically approved or merged by this workflow.

After a successful automatic squash merge, the source branch SHOULD be deleted when GitHub permits it.

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
