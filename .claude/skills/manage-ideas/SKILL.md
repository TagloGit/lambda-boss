---
name: manage-ideas
description: "Sync LAMBDA ideas between Notion and GitHub Issues, refresh the Current Lambdas catalogue in Notion, and close idea issues whose LAMBDA has shipped. Usage: /manage-ideas"
allowed-tools: Bash, Read, Glob, Grep, mcp__claude_ai_Notion__notion-fetch, mcp__claude_ai_Notion__notion-search, mcp__claude_ai_Notion__notion-create-pages, mcp__claude_ai_Notion__notion-move-pages, mcp__claude_ai_Notion__notion-update-page
---

# Manage Ideas — lambda-boss

Keep the Notion ⇄ GitHub idea bridge in sync, in both directions, doing the **least work necessary**:

- **Inbound**: sync new LAMBDA ideas from Notion to GitHub Issues.
- **Outbound**: refresh the "Current Lambdas" Notion catalogue with the LAMBDAs shipped in this repo.
- **Reconcile**: close `lambda-idea` issues whose LAMBDA has just shipped.

The repo is version-controlled, so **git is the source of truth for what changed**. The catalogue page stamps the commit it was last built from; each run diffs against that commit and only touches what moved. A run where nothing changed and no new idea exists should do almost nothing.

## Key Notion page IDs

| Page | ID |
| --- | --- |
| Excel eSports (parent) | `3407b3d23d2f80e49224f99b577224db` |
| LAMBDA ideas (inbox) | `3407b3d23d2f80aa9ac4e4903868d7c8` |
| Current Lambdas (catalogue) | `3437b3d23d2f819595bac162a5268640` |
| Synced to GitHub (archive, child of LAMBDA ideas) | discovered at runtime — created if missing |

---

## Step 0 — Detect what actually needs doing

Run these cheap probes first, then run only the parts that have work.

1. **Fetch the catalogue page once** (used for the sync marker *and* as the row cache for delta rebuilds):

   ```
   notion-fetch id: 3437b3d23d2f819595bac162a5268640
   ```

   Extract the **last-synced commit SHA** from the header line:

   ```
   _Last synced: <YYYY-MM-DD> · commit `<short-sha>`_
   ```

   If the header has **no commit SHA** (e.g. a catalogue built by the old version of this skill), treat the outbound delta as a **full rebuild** (Step A, full path).

2. **Compute the lambda delta** against that SHA:

   ```bash
   git rev-parse --short HEAD
   ```
   ```bash
   git diff --name-status <last-synced-sha> HEAD -- lambdas
   ```

   Keep only `*.lambda` paths. Classify: `A` = added, `M` = modified, `D` = deleted, `R…` = rename (treat as delete-old + add-new). If `<last-synced-sha>` is missing or `git diff` errors (unknown SHA), fall back to a full rebuild.

3. **Probe the inbound inbox** for unsynced ideas:

   ```
   notion-fetch id: 3407b3d23d2f80aa9ac4e4903868d7c8
   ```

   Count child `<page>` elements, excluding the "Synced to GitHub" archive container.

Decide the plan:

| Condition | Run |
| --- | --- |
| No lambda delta **and** no SHA-missing fallback | Skip Step A (report "catalogue unchanged") |
| Lambda delta present (or SHA missing) | Step A |
| Any **added** (`A`) lambdas | Step B (reconcile) |
| Any unsynced child pages | Step C (inbound) |

State the plan in one line before doing the work, e.g. _"2 lambdas changed, 1 added, 1 new idea → refresh catalogue, reconcile 1, sync 1."_

---

## Step A — Refresh "Current Lambdas" (delta rebuild)

The catalogue is grouped by library (H2), each with a three-column table: **Name** | **Description** | **Calculation**. Only *re-derive* the rows that changed; unchanged rows are left exactly as they are on the page.

### A1 — Leave unchanged rows alone

From the catalogue markdown fetched in Step 0, locate the existing rows keyed by LAMBDA name so you know where the changed ones sit. Every lambda **not** in the delta keeps its existing row text untouched — do **not** re-read its `.lambda` file, and do **not** re-emit its row.

(Full-rebuild fallback: when there is no SHA marker, skip A1 — every lambda is "changed", so derive all rows in A2 and take the full-rebuild path in A3.)

### A2 — Derive rows for the delta only

For each added/modified `.lambda` file in the delta, read it and extract:

- **Name** — the function name from the `FUNCTION NAME:` header.
- **Description** — the one-line blurb inside the `/**...*/` JavaDoc comment on line 2.
- **Calculation** — the meaningful formula logic from the `//  Procedure` section, **excluding**:
  - the `Help` TEXTSPLIT block,
  - the `//  Check inputs` bindings (`Help?`, `ISOMITTED(...)` guards, default-value normalisers like `IF(ISOMITTED(x), default, x)`),
  - the final `IF(Help?, Help, result)` return.

  Collapse to a compact inline formula. Intermediate LET bindings can be inlined or described in prose (e.g. "… where `_count = CEILING(LEN(text)/_size, 1)`"). Aim for a single backticked expression per row; prose only when inlining would hurt readability. Prefer fidelity over prettiness — the reader is checking for duplicate *logic*, so the formula must be recognisable, not necessarily runnable.

For each **deleted/renamed-away** lambda, drop its row.

For a library that gains its first lambda, or whose folder is new, read `lambdas/<library>/_library.yaml` for its `name` and `description`. (Library headers/descriptions for unchanged libraries are reused from the fetched page.)

### A3 — Write the changes back

**Default: targeted `update_content` edits.** The catalogue is ~100 table rows returned as Notion table markup; re-emitting all of them to change one or two risks silently mangling rows that never moved. So write only what changed, as a batch of search-and-replace operations in a single `notion-update-page` call:

- **The sync marker** — two edits: the old date → today's `YYYY-MM-DD`, and the old short SHA → the `git rev-parse --short HEAD` value from Step 0. Match on the bare date and bare SHA strings (they are unique on the page); don't try to match the whole italicised header line, whose markdown round-trips inconsistently.
- **A changed row** — match its `<td>NAME</td>` cell and replace the row.
- **An added row** — anchor on the `<tr>` of the row it should precede (alphabetical by LAMBDA name within its library) and emit `<new row>` + `<anchor row opening>` as the replacement.
- **A removed row** — match the whole `<tr>…</tr>` and replace with empty.
- **A new library** — anchor on the `## <NextLibrary>` heading that should follow it and prepend the new H2, its italicised description, and its table.

```
notion-update-page
  page_id: 3437b3d23d2f819595bac162a5268640
  command: update_content
  content_updates: [ {old_str: "2026-07-24", new_str: "2026-07-28"},
                     {old_str: "5bdaa70", new_str: "3275409"},
                     {old_str: "<tr>\n<td>MAXOCCUR</td>", new_str: "<tr>\n<td>MATCHSPLIT</td>\n…\n</tr>\n<tr>\n<td>MAXOCCUR</td>"} ]
```

The `<short-sha>` **must** be the `git rev-parse --short HEAD` value from Step 0 — that is what the next run diffs against. If a marker edit fails to match, stop and report it rather than leaving the page stamped with a stale SHA: a wrong marker makes the *next* run's delta wrong too.

**Full-rebuild path only** (no SHA marker in Step 0): build the whole page — one H2 per library sorted alphabetically, library `description` in italics, rows sorted alphabetically by LAMBDA name — under this header block:

```markdown
Catalogue of LAMBDAs already implemented in [TagloGit/lambda-boss](https://github.com/TagloGit/lambda-boss). Check this list before proposing new LAMBDA ideas to avoid duplicates.

_Last synced: <today YYYY-MM-DD> · commit `<short-sha from git rev-parse>`_
```

and send it with `command: replace_content`, `new_str: <the reassembled markdown>`. This is the one case where a full rewrite is correct — there is no trustworthy prior state to preserve.

---

## Step B — Reconcile shipped ideas (close implemented idea issues)

Run only when the delta contains **added** (`A`) lambdas — a newly shipped LAMBDA may be the implementation of an open idea issue that was never linked with `Closes #NN`.

1. List open idea issues:

   ```bash
   gh issue list -R TagloGit/lambda-boss --label lambda-idea --state open --json number,title,body
   ```

2. For each newly added LAMBDA, judge whether it implements one of those open issues — match on function name and intent (the issue title is `LAMBDA idea: <name>`; compare against the LAMBDA's name and description). Be conservative: only act on a **confident** match.

3. For a confident match, close the issue and mark it done:

   ```bash
   gh issue edit <NN> -R TagloGit/lambda-boss --add-label "status: done" --remove-label "status: backlog"
   ```
   ```bash
   gh issue close <NN> -R TagloGit/lambda-boss --comment "Shipped as LAMBDA \`<NAME>\` (\`lambdas/<lib>/<NAME>.lambda\`). Closing the idea."
   ```

   (Remove whichever status label the issue currently carries — usually `status: backlog`.)

4. For an **uncertain** match, do not touch the issue; list it under "Possible matches — needs Tim" in the report.

---

## Step C — Sync ideas inbound (Notion → GitHub)

Run only when Step 0 found unsynced child pages.

### C1 — Ensure the "Synced to GitHub" archive exists

From the inbox fetch in Step 0, check for a child page titled "Synced to GitHub". If absent, create it and save its ID for C4:

```
notion-create-pages
  parent: { type: "page_id", page_id: "3407b3d23d2f80aa9ac4e4903868d7c8" }
  pages: [{ properties: { "title": "Synced to GitHub" }, content: "Archive of LAMBDA ideas that have been synced to GitHub Issues." }]
```

### C2 — Cheap duplicate check (names, not the catalogue)

Build the set of existing LAMBDA names from a filename Glob — `lambdas/**/*.lambda`, take each basename without extension. This is all the dedup check needs; **do not** rebuild or re-read the catalogue for this. (Step A, if it ran, already gives you the names too.)

### C3 — For each unsynced idea page

```
notion-fetch id: <page-id>
```

**Duplicate check** — compare the idea's title/intent against the existing LAMBDA names from C2. If it clearly duplicates an existing LAMBDA:

- Leave the Notion page where it is (do not archive it).
- Add it to the "Skipped (duplicates)" section of the report, noting the LAMBDA it matches.

Otherwise, create the GitHub Issue:

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

### C4 — Move synced page to archive

After the issue is created successfully:

```
notion-move-pages
  page_or_database_ids: ["<page-id>"]
  new_parent: { type: "page_id", page_id: "<synced-to-github-page-id>" }
```

---

## Step D — Report results

Summarise only what ran:

```
Catalogue: refreshed (3 rows changed, 1 added, 0 removed) at commit a1b2c3d.
  — or — Catalogue: unchanged (no lambda changes since a1b2c3d).

Reconciled (closed implemented idea issues):
- TagloGit/lambda-boss#NN "Weighted Score Calculator" — shipped as WSCORE

Synced N idea(s) from Notion to GitHub:
- "Weighted Score Calculator" -> TagloGit/lambda-boss#NN

Skipped as duplicates:
- "Running Total" — matches existing CUMSUMGRID

Possible matches — needs Tim:
- TagloGit/lambda-boss#MM "Foo" — maybe shipped as FOOBAR? (unsure)
```

If a section is empty, omit it. If nothing ran at all: "Nothing to do — catalogue current, no new ideas."

## Idempotency

- The catalogue is edited from git state and stamped with HEAD's SHA; a re-run with no new commits finds an empty delta and skips Step A.
- Ideas already moved to "Synced to GitHub" don't appear as inbox children next run.
- The "Synced to GitHub" page is created only if missing.
- Reconciliation only closes issues on a confident match and is a no-op once they're closed.

## Behaviour Notes

- Do NOT modify the content of idea pages — sync them as-is.
- Do NOT create specs or plans from ideas — they go to the backlog for triage.
- Strip the "LAMBDA: " prefix from idea titles when creating issues (use "LAMBDA idea: <name>").
- If a Notion page fetch fails, skip it and report the error — don't stop the whole sync.
- If an `update_content` edit fails to match its anchor, re-fetch the catalogue and retry that one edit against the actual text — never escalate to a full `replace_content` rewrite just to force a small change through.
- The first run after the SHA marker was introduced finds no marker → one full rebuild that plants it; every run after is incremental.
