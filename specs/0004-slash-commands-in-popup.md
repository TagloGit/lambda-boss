# 0004 — Slash commands in the main popup

## Problem

Lambda Boss has several commands — Load Library, LET to LAMBDA, Edit Lambda (spec 0003), Settings — that are currently only reachable via the Excel ribbon. Power users who invoke the main popup with a keyboard shortcut want to reach every Lambda Boss action without leaving the keyboard or dropping into the ribbon. Today the popup only does library browsing and Lambda search.

## Proposed Solution

Extend the main popup (`LambdaPopup`) with a third mode — **Commands** — activated by typing `/` as the first character in the search box. The Commands mode shows a fuzzy-filterable list of Lambda Boss actions. Enter executes the selected command (which usually closes the popup and runs the action on the Excel side).

### Activation and exit

- Typing `/` as the **first** character of the search box switches the popup into Commands mode and starts filtering the command list by whatever follows the `/`.
- Deleting back to an empty search box or to a non-`/` first character exits Commands mode and returns to the mode that was active before (Library or Search).
- Tab does not toggle Commands mode (Tab remains the Library ↔ Search toggle).
- Mode indicators at the top show "Libraries · Search · Commands", with the active mode highlighted.

### v1 commands

| Command name (shown in list) | Action |
|---|---|
| `LET to LAMBDA` | Runs `ConvertLetToLambdaCommand.Run()` |
| `Edit Lambda` | Runs the Edit Lambda command from spec 0003 |
| `Load Library` | Switches popup to Library mode (does not close) |
| `Settings` | Runs `ShowLambdaPopupCommand.ShowSettings()` |

Filtering uses the existing `FuzzyMatcher` against the command name.

### Execution semantics

- Commands that operate on a cell (LET to LAMBDA, Edit Lambda) hide the popup first, then execute on Excel's side. Error messages surface via the existing message-box path in those commands.
- Commands that change popup mode (Load Library) switch mode without closing.
- Settings opens the settings window on the same STA thread, as today.

## User Stories

- As a keyboard-driven user, I want to trigger any Lambda Boss action from the main popup via `/`-prefixed commands, so that I can stay on the keyboard for my whole workflow.
- As a new user, I want a discoverable list of available actions by typing `/`, so that I don't have to memorize ribbon locations or shortcuts.

## Acceptance Criteria

- [ ] Typing `/` as the first character of the search box switches the popup into Commands mode and displays the command list.
- [ ] The command list filters as the author types (`/le` matches `LET to LAMBDA`).
- [ ] Enter executes the selected command; arrow keys navigate; Escape closes the popup (or exits Commands mode first — confirmed in plan).
- [ ] Deleting back to empty or a non-`/` first character exits Commands mode and restores the previously active mode.
- [ ] The mode indicator row shows "Libraries · Search · Commands" with the active mode highlighted.
- [ ] All four v1 commands work end-to-end from the popup.
- [ ] `LET to LAMBDA` and `Edit Lambda` close the popup before running.
- [ ] `Load Library` switches popup mode without closing.
- [ ] `Settings` opens the settings window and closes the popup.
- [ ] When the Edit Lambda ribbon button doesn't yet exist (i.e. spec 0003 isn't landed), the `Edit Lambda` slash command is the only way to reach it — or we gate this spec on 0003. The plan should sequence accordingly.

## Out of Scope

- Slash commands accepting arguments (e.g. `/load MyLibrary`). v1 is noun-only.
- User-customizable command lists or keybindings.
- Slash commands in other windows (e.g. `LetToLambdaWindow`).
- Treating `/` inside a query (not first character) as a special character.

## Open Questions

- Should Escape in Commands mode exit back to the previous mode (two-step exit), or close the popup outright (one-step, matching other modes)? Tentative: two-step — Escape exits Commands mode first, then a second Escape closes the popup. Aligns with the popup's prefix-prompt pattern.
- Ordering: this spec depends on spec 0003 for the `Edit Lambda` command. If 0003 isn't implemented when this is picked up, the `Edit Lambda` entry should be omitted rather than stubbed.
