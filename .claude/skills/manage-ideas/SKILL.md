---
name: manage-ideas
description: "Sync LAMBDA ideas from Notion to GitHub Issues. Usage: /manage-ideas"
allowed-tools: Bash, Read, Glob, Grep, mcp__claude_ai_Notion__notion-fetch, mcp__claude_ai_Notion__notion-search, mcp__claude_ai_Notion__notion-create-pages, mcp__claude_ai_Notion__notion-move-pages
---

# Manage Ideas — lambda-boss

Sync LAMBDA ideas from Notion to GitHub Issues, completing the idea capture bridge.

## Flow

1. Fetch the "LAMBDA ideas" Notion page (ID: `3407b3d23d2f80aa9ac4e4903868d7c8`)
2. Identify child pages — these are unsynced idea pages
3. Ignore any child page titled "Synced to GitHub" — that is the archive folder
4. For each unsynced idea page:
   a. Fetch the full page content
   b. Create a GitHub Issue on `TagloGit/lambda-boss` with the idea content
   c. Move the Notion page into the "Synced to GitHub" child page
5. Report what was synced

## Instructions

### Step 1 — Fetch the LAMBDA ideas page

```
notion-fetch id: 3407b3d23d2f80aa9ac4e4903868d7c8
```

Parse the child pages from the `<content>` block. Each `<page>` element is a potential idea. Extract the page URL/ID and title.

Skip any child page whose title is "Synced to GitHub" — this is the archive container.

If there are no unsynced child pages, report "No new ideas to sync" and stop.

### Step 2 — Ensure "Synced to GitHub" page exists

Check if a child page titled "Synced to GitHub" already exists under the LAMBDA ideas page (from Step 1 results).

If it does NOT exist, create it:

```
notion-create-pages
  parent: { type: "page_id", page_id: "3407b3d23d2f80aa9ac4e4903868d7c8" }
  pages: [{ properties: { "title": "Synced to GitHub" }, content: "Archive of LAMBDA ideas that have been synced to GitHub Issues." }]
```

Save the page ID of the "Synced to GitHub" page for use in Step 4.

### Step 3 — For each unsynced idea page

Fetch the full content of the idea page:

```
notion-fetch id: <page-id>
```

Convert the Notion content into a GitHub Issue body using this template:

```markdown
## Origin

Synced from Notion: [<page-title>](<page-url>)

## Content

<paste the idea page content here, converted to GitHub-flavoured markdown>
```

Create the GitHub Issue:

```bash
gh issue create -R TagloGit/lambda-boss \
  --title "<page-title>" \
  --label "enhancement" --label "lambda-idea" --label "status: backlog" \
  --body "<issue-body>"
```

Use a HEREDOC for the body to preserve formatting:

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

### Step 4 — Move synced page to archive

After the issue is created successfully, move the Notion page to "Synced to GitHub":

```
notion-move-pages
  page_or_database_ids: ["<page-id>"]
  new_parent: { type: "page_id", page_id: "<synced-to-github-page-id>" }
```

### Step 5 — Report results

Summarise what was done:

```
Synced N idea(s) from Notion to GitHub:
- "Weighted Score Calculator" -> TagloGit/lambda-boss#NN
```

If nothing was synced: "No new ideas to sync."

## Idempotency

This skill is safe to run repeatedly:
- Ideas already moved to "Synced to GitHub" won't appear as child pages of "LAMBDA ideas" on the next run
- The "Synced to GitHub" page is created only if it doesn't exist

## Behaviour Notes

- Do NOT modify the content of idea pages — sync them as-is
- Do NOT create specs or plans from ideas — they go to the backlog for triage
- Strip the "LAMBDA: " prefix from page titles when creating issue titles (use "LAMBDA idea: <name>" format)
- If a Notion page fetch fails, skip it and report the error — don't stop the whole sync
