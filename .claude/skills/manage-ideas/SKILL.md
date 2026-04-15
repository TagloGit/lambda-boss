---
name: manage-ideas
description: "Sync LAMBDA ideas between Notion and GitHub Issues, and refresh the Current Lambdas catalogue in Notion. Usage: /manage-ideas"
allowed-tools: Bash, Read, Glob, Grep, mcp__claude_ai_Notion__notion-fetch, mcp__claude_ai_Notion__notion-search, mcp__claude_ai_Notion__notion-create-pages, mcp__claude_ai_Notion__notion-move-pages, mcp__claude_ai_Notion__notion-update-page
---

# Manage Ideas — lambda-boss

Keep the Notion ⇄ GitHub idea bridge in sync, in both directions:

- **Inbound**: sync new LAMBDA ideas from Notion to GitHub Issues.
- **Outbound**: refresh the "Current Lambdas" Notion page with the LAMBDAs already shipped in this repo, so new ideas can be vetted for duplication.

## Key Notion page IDs

| Page | ID |
| --- | --- |
| Excel eSports (parent) | `3407b3d23d2f80e49224f99b577224db` |
| LAMBDA ideas (inbox) | `3407b3d23d2f80aa9ac4e4903868d7c8` |
| Current Lambdas (catalogue) | `3437b3d23d2f819595bac162a5268640` |
| Synced to GitHub (archive, child of LAMBDA ideas) | discovered at runtime — created if missing |

## Flow

1. **Refresh Current Lambdas** — rebuild the Notion catalogue from the repo so the Outbound view is current.
2. **Sync ideas inbound** — fetch `LAMBDA ideas`, create GitHub Issues for any unsynced child pages, move them to `Synced to GitHub`.
3. Report what was done in both directions.

---

## Part A — Refresh "Current Lambdas" (Outbound)

### A1 — Enumerate the LAMBDAs in the repo

Use Glob on `lambdas/**/*.lambda`. Group by the library folder (e.g. `array`, `string`). For each library, also read `lambdas/<library>/_library.yaml` to get its `name` and `description`.

For every `.lambda` file, extract:

- **Name** — the function name, from the `FUNCTION NAME:` header.
- **Description** — the one-line blurb inside the `/**...*/` JavaDoc comment on line 2.
- **Calculation** — the meaningful formula logic from the `//  Procedure` section, **excluding**:
  - the `Help` TEXTSPLIT block,
  - the `//  Check inputs` bindings (`Help?`, `ISOMITTED(...)` guards, default-value normalisers like `IF(ISOMITTED(x), default, x)`),
  - the final `IF(Help?, Help, result)` return.

Collapse to a compact inline formula. Intermediate LET bindings can be inlined or described in prose (e.g. "… where `_count = CEILING(LEN(text)/_size, 1)`"). Aim for a single backticked expression per row; use prose only when inlining would hurt readability.

### A2 — Build the page content

Produce Notion-flavoured Markdown with one H2 per library (sorted alphabetically by library name), each containing:

- The library's `description` in italics.
- A three-column table: **Name** | **Description** | **Calculation**.

Rows sorted alphabetically by LAMBDA name.

Header block at the top of the page:

```markdown
Catalogue of LAMBDAs already implemented in [TagloGit/lambda-boss](https://github.com/TagloGit/lambda-boss). Check this list before proposing new LAMBDA ideas to avoid duplicates.

_Last synced: <YYYY-MM-DD>_
```

### A3 — Replace the Current Lambdas page content

```
notion-update-page
  page_id: 3437b3d23d2f819595bac162a5268640
  command: replace_content
  new_str: <the markdown from A2>
```

Use `replace_content` (not `update_content`) so the page is fully rebuilt each run — this is the source of truth.

---

## Part B — Sync ideas inbound (Notion → GitHub)

### B1 — Fetch the LAMBDA ideas page

```
notion-fetch id: 3407b3d23d2f80aa9ac4e4903868d7c8
```

Parse the child pages from the `<content>` block. Each `<page>` element is a potential idea. Extract the page URL/ID and title.

Skip any child page titled "Synced to GitHub" — this is the archive container.

If there are no unsynced child pages, skip to Part C.

### B2 — Ensure "Synced to GitHub" archive page exists

Check whether a child page titled "Synced to GitHub" already exists under the LAMBDA ideas page (from Step B1 results).

If it does NOT exist, create it:

```
notion-create-pages
  parent: { type: "page_id", page_id: "3407b3d23d2f80aa9ac4e4903868d7c8" }
  pages: [{ properties: { "title": "Synced to GitHub" }, content: "Archive of LAMBDA ideas that have been synced to GitHub Issues." }]
```

Save the page ID of the "Synced to GitHub" page for use in Step B4.

### B3 — For each unsynced idea page

Fetch the full content of the idea page:

```
notion-fetch id: <page-id>
```

**Duplicate check** — compare the idea's title/intent against the Current Lambdas catalogue you just rebuilt in Part A. If it clearly duplicates an existing LAMBDA, do NOT create an issue. Instead:

- Leave the Notion page where it is (do not archive it).
- Add it to the "Skipped (duplicates)" section of the final report, noting the existing LAMBDA it matches.

Otherwise, convert the Notion content into a GitHub Issue body:

```markdown
## Origin

Synced from Notion: [<page-title>](<page-url>)

## Content

<paste the idea page content here, converted to GitHub-flavoured markdown>
```

Create the GitHub Issue with a HEREDOC body:

```bash
gh issue create -R TagloGit/lambda-boss \
  --title "LAMBDA idea: Weighted Score Calculator" \
  --label "enhancement" --label "lambda-idea" --label "status: backlog" \
  --body "$(cat <<'EOF'
## Origin

Synced from Notion: [LAMBDA: Weighted Score Calculator](https://www.notion.so/...)

## Content

...
EOF
)"
```

### B4 — Move synced page to archive

After the issue is created successfully, move the Notion page to "Synced to GitHub":

```
notion-move-pages
  page_or_database_ids: ["<page-id>"]
  new_parent: { type: "page_id", page_id: "<synced-to-github-page-id>" }
```

---

## Part C — Report results

Summarise both directions:

```
Current Lambdas refreshed: N LAMBDAs across M libraries.

Synced N idea(s) from Notion to GitHub:
- "Weighted Score Calculator" -> TagloGit/lambda-boss#NN

Skipped as duplicates:
- "Running Total" — matches existing CUMSUMGRID
```

If nothing was synced and nothing was skipped: "No new ideas to sync."
If the catalogue was unchanged from last run, still report "Current Lambdas refreshed".

## Idempotency

This skill is safe to run repeatedly:
- Part A rebuilds the catalogue every run — content is replaced, not appended.
- Ideas already moved to "Synced to GitHub" won't appear as child pages of "LAMBDA ideas" on the next run.
- The "Synced to GitHub" page is created only if it doesn't exist.

## Behaviour Notes

- Do NOT modify the content of idea pages — sync them as-is.
- Do NOT create specs or plans from ideas — they go to the backlog for triage.
- Strip the "LAMBDA: " prefix from page titles when creating issue titles (use "LAMBDA idea: <name>" format).
- If a Notion page fetch fails, skip it and report the error — don't stop the whole sync.
- When extracting the Calculation column, prefer fidelity over prettiness — the reader is checking for duplicate *logic*, so the formula must be recognisable, not necessarily runnable.
