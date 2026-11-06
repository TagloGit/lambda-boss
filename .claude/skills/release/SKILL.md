---
name: release
description: "Cut a lambda-boss release: frozen release/<date> PR to main, Tim's approval, merge commit, tag, unsigned zip. Usage: /release"
allowed-tools: Bash, Read
---

# Release — lambda-boss

`main` changes only here. It's what Stable users, Tim included, read live, so a merge ships the library at once.

## Before cutting

- **Tim said so, in words.** Default cadence is every week or two, on his call.
- **Competition freeze:** no release in the days before an MEWC round. If one is near, stop and say so.
- CI is green on `develop`'s head.
- `Version` in `Directory.Build.props` on `develop` is the version to release, and its tag doesn't exist yet.

## Cut

```bash
scripts/cut-release.sh --dry-run
scripts/cut-release.sh
```

This pushes `release/<date>` at `develop`'s head and opens the PR to `main` with reviewer `taglotacks`. The snapshot is frozen: later `develop` merges don't join it. To add something urgent, close the PR and cut again.

Rewrite the PR body's first line with **what Tim must do on his PC**, or "Nothing on your PC." Then group the changes by what he'd notice, and link the Labs features he can try. No questions for him in the body.

## After Tim approves

The approval is his; the rest is agent work. Wait for `build-and-test` on the PR first.

```bash
gh pr merge <n> --repo TagloGit/lambda-boss --merge
```

Never use `--admin`, and never approve it yourself. The merge is refused until the approval is there; a refusal is a stop.

```bash
git fetch origin main
git tag v<version> origin/main
git push origin v<version>
```

The `Release` workflow checks that the tag matches the props version and is on `main`, then builds, tests and publishes `LambdaBoss-<version>.zip` as the latest GitHub Release. Check that it did:

```bash
gh release view v<version> --repo TagloGit/lambda-boss
```

## Close out

Raise a one-line PR to `develop` bumping `Version` in `Directory.Build.props` to the next version. It needs green CI and no review. Update the status labels on any issue the release was waiting for.
