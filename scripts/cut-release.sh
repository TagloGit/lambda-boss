#!/usr/bin/env bash
# Cuts a release: pushes release/<date> at develop's head and opens the PR to main.
# Usage: scripts/cut-release.sh [--dry-run]
# Steps before and after this one: .claude/skills/release/SKILL.md
set -euo pipefail

REPO=TagloGit/lambda-boss
DRY_RUN=false
if [[ "${1:-}" == "--dry-run" ]]; then DRY_RUN=true; fi

git fetch origin develop main --tags

head=$(git rev-parse origin/develop)
version=$(git show origin/develop:Directory.Build.props | sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p')
if [[ -z "$version" ]]; then
    echo "Couldn't read <Version> from develop's Directory.Build.props." >&2
    exit 1
fi

if git rev-parse -q --verify "refs/tags/v$version" > /dev/null; then
    echo "v$version already exists: bump Version on develop first." >&2
    exit 1
fi

ci=$(gh run list --repo "$REPO" --workflow CI --branch develop --commit "$head" --json conclusion --jq '.[0].conclusion // ""')
if [[ "$ci" != "success" ]]; then
    echo "CI on develop's head ($head) isn't green yet: '$ci'." >&2
    exit 1
fi

branch="release/$(date -u +%Y-%m-%d)"
if git ls-remote --exit-code --heads origin "$branch" > /dev/null; then
    branch="$branch-$(date -u +%H%M)"
fi

changes=$(git log --no-merges --format='- %s' "origin/main..$head")
body=$(cat <<BODY
<Tim's part, on the first line: anything he must do on his PC, or "Nothing on your PC.">

Release v$version: a frozen snapshot of \`develop\` at $head.

## Changes

$changes

After approval an agent merges with a merge commit, tags v$version, and CI publishes the unsigned zip.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
BODY
)

if $DRY_RUN; then
    echo "Would push $head to $branch and open this PR to main:"
    echo
    echo "$body"
    exit 0
fi

git push origin "$head:refs/heads/$branch"
gh pr create --repo "$REPO" --base main --head "$branch" --reviewer taglotacks \
    --title "Release v$version" --body "$body"
