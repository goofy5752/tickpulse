---
name: tickpulse-pr-workflow
description: Use BEFORE starting any feature, fix, chore or docs change in the TickPulse repo, and BEFORE any git commit, push or PR. Triggers - asked to "implement / add / build / fix / wire up / document" something, about to git commit/push, or integrating finished work. Covers worktree-per-change, keeping the worktree synced to the latest `origin/main`, local test gating, and opening a pull request to `main`. Several agents may work this repo concurrently.
---

# TickPulse PR Workflow

## Rule

**`main` is the only long-lived branch. Every change reaches it through one pull request, made
from an isolated git worktree that is kept in sync with the latest `origin/main`.**

Several agents or terminals may work this repo at the same time. So each change gets its own
**worktree** (not just a branch on a shared checkout), the worktree is based on and re-synced to
the freshest `origin/main`, conflicts are resolved locally, and the change is delivered as a PR
targeting `main`. `main` is protected: the `backend` CI check must pass. No direct edits,
commits or pushes on `main`.

Breaking the letter of this (one "tiny" direct commit, basing off a stale local `main`, opening a
PR before tests pass, leaving conflicts for the merge) breaks the spirit of it. There are no
exceptions for small changes.

## When this applies

The moment any of these is true, you are in this skill, *before* touching code:

- You were asked to implement / add / build / fix / wire up / refactor / document anything.
- You are about to run `git add`, `git commit` or `git push`.
- You are integrating or "finishing" completed work.
- You are not in an isolated worktree (`git rev-parse --git-dir` equals
  `git rev-parse --git-common-dir` ⇒ you are on the shared checkout; stop and make a worktree).

## The workflow

1. **Worktree first, from the LATEST `origin/main`.** Never branch from a stale local `main`;
   another agent may have just merged. Use the native worktree tool (`EnterWorktree`) or
   `superpowers:using-git-worktrees`; the git fallback is:
   ```bash
   git fetch origin
   git worktree add .claude/worktrees/<slug> -b <type>/<slug> origin/main
   cd .claude/worktrees/<slug>
   ```
   `<type>` = `feat | fix | chore | docs`; `<slug>` is kebab-case and describes the change:
   `feat/sma-window`, `fix/ws-rotation-overlap`, `docs/adr-average-definition`.
   `.claude/worktrees/` is git-ignored.

2. **Make the change**, following `tickpulse-architecture` for anything under `src/` and
   `tickpulse-testing` for how it is tested (TDD for Domain, Testcontainers for persistence).

3. **Commit in scoped Conventional Commits**, one commit per coherent slice:
   ```
   feat(domain): period codes and UTC bucket alignment
   feat(infra): idempotent tick writer with exact candle upsert
   test(api): cover XML problem details for 406
   ```
   `<type>(<scope>): <subject>`; types `feat|fix|chore|test|refactor|docs`; scopes such as
   `domain`, `app`, `infra`, `ingestion`, `api`, `cli`, `web`, `ci`, `deploy`, `docs`.
   Stage with explicit paths (`git add src/... tests/...`), never `git add -A` on a checkout
   that others may share. Never `--no-verify`. Never bypass hooks.

4. **Re-sync with `origin/main` before pushing**, because another agent may have merged while you
   worked. Merge the latest `main` into the worktree, resolve conflicts, then re-run the
   affected test layers:
   ```bash
   git fetch origin
   git merge origin/main          # resolve conflicts NOW, locally
   ```

5. **Get the touched layers green locally.** Run what CI runs for the parts your diff touches:
   ```bash
   dotnet format TickPulse.slnx --verify-no-changes      # always, for any .cs/.csproj change
   dotnet build TickPulse.slnx -c Release
   dotnet test TickPulse.slnx                            # needs Docker running (Testcontainers)
   npm --prefix web run lint                             # if web/** changed
   npm --prefix web run typecheck
   npm --prefix web run build
   ```
   Green means you saw it pass. Do not infer it (`superpowers:verification-before-completion`).
   If a layer cannot run locally (for example Docker is down), say so explicitly in the PR.

6. **Push the branch and open the PR to `main`:**
   ```bash
   git push -u origin <type>/<slug>
   gh pr create --base main --title "<type>: <concise change>" --body-file <file>
   ```
   Fill the template in `.github/pull_request_template.md` (Summary / Changes / Tests run /
   Risks). GitHub appends `(#PR)` on squash-merge, producing `<type>: <change> (#PR)` on `main`.

7. **Keep the PR mergeable.** If `main` advances before the PR lands, repeat step 4 (fetch +
   merge `origin/main`, resolve, re-verify) so the PR is always conflict-free and up to date
   (branch protection is `strict`: the branch must be current with `main`).

8. **Stop at "PR opened."** Do not merge unless explicitly told to. Report the PR URL and which
   test layers you ran. When told to merge: wait for CI green, then
   `gh pr merge <n> --squash --delete-branch`, and remove the worktree
   (`git worktree remove .claude/worktrees/<slug>`).

## Conventions

| Thing             | Convention                                                     |
| ----------------- | -------------------------------------------------------------- |
| Isolation         | One **git worktree** per change, under `.claude/worktrees/`    |
| Base / target     | `origin/main`, always                                          |
| Branch            | `feat/<slug>`, `fix/<slug>`, `chore/<slug>`, `docs/<slug>`     |
| Commits on branch | `<type>(<scope>): <subject>` (Conventional Commits, scoped)    |
| PR title          | `<type>: <concise change>`; `(#PR)` appended on squash-merge   |
| PR body           | `.github/pull_request_template.md`                             |
| Required check    | `backend` (plus `web` once the dashboard exists)               |
| Merge style       | Squash, delete branch                                          |
| Remote            | `origin` → `goofy5752/tickpulse` (`gh` CLI available)          |

## Rationalizations (all false)

| Excuse | Reality |
|--------|---------|
| "I'll just edit on the shared checkout." | Other agents may be running. Isolate in a worktree or you collide. |
| "Local `main` is fine, no need to fetch." | Another agent may have merged seconds ago. Base on, and re-sync to, `origin/main`. |
| "It's one line, a PR is overkill." | Every change is a PR to `main`. Size is irrelevant. |
| "I'll resolve conflicts at merge time." | Resolve them locally before push and again before merge. |
| "Tests need Docker, CI will run them." | Local green gates the PR. Start Docker; if you truly cannot, say so in the PR. |
| "`dotnet format` is cosmetic." | CI runs `--verify-no-changes` and fails the build on any diff. |
| "I'll squash-merge it myself." | Not unless explicitly told to. |

## Red flags (STOP)

- You are editing on the shared checkout, not a worktree.
- You branched from a local `main` without `git fetch origin` first.
- `git push origin main`, or any commit made directly on `main`.
- `git commit --no-verify` or any hook bypass.
- `git add -A` on a checkout other agents may be using.
- Pushing or opening a PR without re-syncing to `origin/main` and resolving conflicts.

## Checklist before saying "done"

- [ ] Work is in a **worktree** on a `feat|fix|chore|docs/<slug>` branch based on `origin/main`.
- [ ] Re-synced with `origin/main` (fetched + merged, conflicts resolved) before push.
- [ ] Commits are scoped Conventional Commits; no `--no-verify`.
- [ ] `dotnet format --verify-no-changes`, Release build and every touched test layer passed locally (you saw it).
- [ ] Web: `lint`, `typecheck`, `build` green if `web/**` changed.
- [ ] Branch pushed; PR opened with `--base main` and the template filled.
- [ ] PR is conflict-free against current `main`; not merged unless told; PR URL + layers run reported.
