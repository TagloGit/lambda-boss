# CLAUDE.md — lambda-boss

Excel add-in for accessing GitHub Lambda libraries

## Repo purpose

TODO: Describe what this repo contains.

## Tech stack

TODO: List languages, frameworks, and key dependencies.

## Build & test

TODO: Add build and test commands, e.g.:
- `npm run dev` — start dev server
- `npm run build` — production build
- `npm test` — run tests

## Conventions

- `/code-review <pr>` — PR code review
- Specs: `specs/`, Plans: `plans/`
- Default branch: `main`
- **Never use compound Bash commands** (no `&&`, `;`, or `|` chaining). Use separate Bash tool calls instead — independent calls can run in parallel. Compound commands trigger extra permission prompts.
- **Never prefix Bash commands with `cd`**. The working directory is already the project root. All commands (`gh`, `git`, `npm`, etc.) work without `cd`.
