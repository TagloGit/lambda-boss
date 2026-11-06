# 0012: develop, Labs, VM staging, dev builds and releases

Record of #390 (Tim's decisions, 6 Nov 2026). The current rules are in `CLAUDE.md`, "How work ships".

## Why

Every merge went to `main` with Tim's approval and shipped to him at once, because the add-in reads the library live from `main`. Tim wants agents to take features from design to tested and refined on their own, and to bring him finished work.

## What was decided

Tim's decisions are in #390. Choices the lead made, each reversible:

- **Only this repo follows the channel.** Other repos have no Labs branch and stay on `main`.
- **Each branch has its own cache folder**, so switching channel never shows the other channel's files. Stable keeps the existing folder.
- **Contents calls name their branch.** They used to rely on the default branch. With `develop` as default, a Stable add-in would have listed `develop`'s files and fetched `main`'s.
- **The channel and switches live in the settings window.** A ribbon button shows the current channel and opens it, so the channel is visible before a competition.
- **The example Labs switch** shows the channel in the popup's status bar.
- **On Labs, the update check compares commits with `dev-latest`.** Dev builds share a base version, so only the commit tells them apart. The check runs at startup.
- **`Directory.Build.props` holds the next release's version**, bumped on `develop` after each release. Bumping on the release branch would conflict with `main` at the following release. This release is 0.2.0, for the new channel behaviour.
- **Releases are built by CI from the tag**, because agents work on a Mac and the build needs Windows. `publish-release.ps1` and its signing steps are gone.
- **`dev-latest` is deleted and recreated** on each merge. `GITHUB_TOKEN` may be refused when moving a tag across workflow changes. The asset is briefly missing during a publish; the updater fails loudly if it hits that gap.
- **The updater `.cmd` works alone**: without its `.ps1` beside it, it fetches one from `develop`, so Tim needs no checkout.
- **`main` needed merge commits allowed** for the release PR: the repo setting and both `main` rulesets' merge methods. The approval rule is unchanged. `develop`'s ruleset stays squash and rebase only.

## Not in scope

- Spec 0011's features: they come later, each behind its own Labs switch.
- `taglo-formula-boss` keeps its setup.
