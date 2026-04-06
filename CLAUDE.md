# CLAUDE.md — lambda-boss

Excel add-in for accessing GitHub Lambda libraries

## Repo purpose

- ExcelDNA add-in
- Library of Lambdas
- Design and test harness for use by Claude Code in the creation of new Lambdas

## Tech stack

TODO

## Build & test

TODO

## Conventions

- Default branch: `main`
- **Never use compound Bash commands** (no `&&`, `;`, or `|` chaining). Use separate Bash tool calls instead — independent calls can run in parallel. Compound commands trigger extra permission prompts.
- **Never prefix Bash commands with `cd`**. The working directory is already the project root. All commands (`gh`, `git`, `npm`, etc.) work without `cd`.
