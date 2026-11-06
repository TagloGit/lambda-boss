---
name: developer
description: "Take a lambda-boss issue from design to proven in real use. Usage: /developer <issue-number>"
allowed-tools: Bash, Read, Write, Edit, Glob, Grep
---

# Developer — lambda-boss

Take an issue from design to built, proven in real use and merged to `develop`. How work ships is in CLAUDE.md, "How work ships".

## Instructions

### Starting Work
1. Read the issue: `gh issue view <number> -R TagloGit/lambda-boss`
2. Check for linked spec/plan docs in the issue body
3. Read spec and plan files if referenced. A feature with no spec gets one in `specs/` as your record; Tim doesn't review it
4. Update issue status:
   `gh issue edit <number> -R TagloGit/lambda-boss --remove-label "status: backlog" --add-label "status: in-progress"`
5. Create a branch from `develop`: `git fetch origin`, then `git checkout -b issue-<number>-short-description origin/develop`

### Working
- Make small, well-described commits
- Follow repo coding standards (see CLAUDE.md)
- Build and test commands are in CLAUDE.md — check there before building
- A new add-in feature goes behind a Labs switch, off by default (`addin/lambda-boss/Labs.cs`). Where a UI choice is open, build the options side by side
- Prove it in the VM on real case or rep files before merging (CLAUDE.md, "How work ships")

### When Blocked
- Add `blocked: tim` label: `gh issue edit <number> -R TagloGit/lambda-boss --add-label "blocked: tim"`
- Add a comment explaining what you need
- Stop work on this issue — do not stall silently

### When You Discover Unplanned Work
- Stop — don't absorb it into the current task
- WIP commit: `WIP: #<number> - paused for new issue`
- Create a new issue for the blocker with `status: backlog`
- Comment on the original issue: "Blocked by #N"
- Decide whether to switch or park it, and say which on the issue

### Raising a PR
1. Push branch: `git push -u origin <branch-name>`
2. Create PR to `develop` with `Closes #<number>` in body and the VM proof
3. Update issue: remove `status: in-progress`, add `status: in-review`
4. Get one independent review (`/code-review`, in a subagent that didn't write the code)
5. With the review's findings fixed and CI green, merge it yourself: `gh pr merge <n> --squash`. No Tim approval. Never merge to `main`: that's the `/release` skill
6. Set `status: done`, delete your branch and worktree

### Coding guidelinse
- Never reference Cell.Formula when manipulating Excel cells, it's a legacy property - use Cell.Formula2 instead.

## Self-Improvement

When you notice a recurring problem, a workflow gap, or something that would help future instances:
1. Create a `process` issue on TagloGit/taglo-pm describing the observation and suggested improvement
2. Reference the specific skill/file that should change (if known)
3. Continue your current work — don't block on the improvement
