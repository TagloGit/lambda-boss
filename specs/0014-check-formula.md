# 0014: Check Formula

Record of #419 (Tim, 9 Nov 2026: "simple formula error-checking more helpful than Excel's could
be transformational"). The lead wrote this as its record; Tim doesn't review it. Choices Tim can
reverse are listed at the end.

## Why

Fixing a formula that won't enter, or that shows `#NAME?`, costs Tim time under the clock, and
Excel never says *where* the problem is.

**Evidence** (excel-esports `analysis/formula-stalls.md`): 8 stalls in Tim's recordings, about
92 s in all. 4 are brackets, about 42 s: three of them a `)` that closes an outer call too soon
(`…<>"I"),nonIce)` closes the LET), one an extra `)` at the end. 4 are unknown names, about 50 s:
`fare` for `fares`, `tyupe` for `types`, `ouyt` declared and `out` used, and `TOCOPOSTOCELL` (two
names run together). Each was a one- or two-character slip with the right answer close by.

**What Excel shows** (recreated in the VM, Excel 365, 9 Nov):

| Stall | Excel's response |
|---|---|
| `)` closes LET early | "You've entered too few arguments for this function." OK, then back in edit mode with the text. |
| `)` closes REDUCE early | "There's a problem with this formula." plus the apostrophe tip. OK, then edit mode. |
| extra `)` at the end, or one missing | "We found a typo in your formula and tried to correct it to: …". Yes/No. |
| `fare`, `ouyt`/`out`, `TOCOPOSTOCELL` | Enters; the cell shows `#NAME?`. Nothing points at the name. |

So the bracket cases happen *while editing* (Excel refuses the formula and leaves Tim in edit
mode) and the name cases happen *after entering*. The command must work in both.

## What Tim sees

With the Labs switch **Check Formula (Ctrl+Shift+K)** (`check-formula`) on:

- **Ctrl+Shift+K** checks the formula being edited, if Tim is editing one (including right after
  Excel's "problem with this formula" dialog), or else the active cell's formula. A cell holding
  `'=…` (a formula entered as text) is checked as a formula too.
- The ribbon's **Check → Check Formula** button checks the active cell.
- A window opens over Excel:
  - a headline: "2 problems found." in red, or "No bracket or name problems found." in green;
  - where the formula came from (`Sheet1!H10`, or "Formula being edited" with the time the
    workbook's names were last read);
  - the whole formula, monospace, each problem's span in red, the bracket it relates to in blue,
    notes in amber;
  - one entry per problem: what it is and the character position, a sentence, an excerpt of
    about 30 characters each side with the span in bold, and the nearest valid name if there is
    one.
- **Esc** (or Enter) closes it and gives focus back to Excel, still editing if it was.

What it reports, brackets first because they're usually the cause:

| Kind | Example message |
|---|---|
| Misplaced `)` | "This comma is outside every function: LET( was already closed by the ) at character 85. That ) is probably misplaced." (the `)` highlighted blue) |
| Extra `)` | "Extra ): nothing is left to close. SUMIFS( at character 2 was closed by the ) at character 89." |
| Unclosed `(` | "SUM( is never closed: a ) is missing, at the end or somewhere inside it." |
| Never closed | an unterminated `"`, `'`, `[` or `{` |
| Argument count (#424) | "REDUCE has 2 arguments; it needs 3. The ) at character 101 closes it."; "IF has 4 arguments; it takes at most 3." |
| LET / LAMBDA shape | "LET has 6 arguments: it needs name, value pairs and then a final calculation…"; a calculation where a LET name should be; a LET name that is a cell reference (`x1`) |
| Unknown name | "fare isn't defined anywhere. Did you mean fares?"; "TOCOPOSTOCELL … contains POSTOCELL: two names run together?"; "x is out of scope here: it's defined at character 18, inside a LAMBDA that has already closed." |
| Library LAMBDA not loaded | "TLOOKUP is a library LAMBDA that isn't loaded in this workbook." |
| Unknown sheet, table or column | "There's no sheet called Shet1. Did you mean Sheet1?"; "Table tblFares has no column called Faer." |
| Note | "ouyt is defined but never used." (the other half of a typo) |

A name resolves if it is a LET name or LAMBDA parameter in scope, a native function, a defined
name (LAMBDAs included), a table, a cell reference, `TRUE`/`FALSE`, or something Excel itself
evaluates to other than `#NAME?` (add-in functions: asked of Excel when checking a cell, and
remembered for checks made mid-edit). Strings, quoted sheet names and structured references are
never mistaken for brackets.

### v2: lead with the fix (#424, #426)

Tim, 9 Nov, on a stray `)` after `TAKE(moves, 5)` that closed REDUCE early: v1 reported three
consequences and not the bracket. Now:

- **Argument counts.** Native functions from a min/max table (`ExcelFunctionArity`, about 200
  functions whose counts are certain; any other function isn't counted, so a wrong entry can't
  raise a false alarm). Library LAMBDAs from their header's `(Required)`/`(Optional)` flags
  (spec 0013): required ones set the minimum, all of them the maximum. A LET or LAMBDA name that
  shadows a function isn't counted. An empty argument (`XLOOKUP(a,b,c,,2)`) counts.
- **Repair search.** When there's a bracket, comma or argument-count fault, the checker tries
  every single `)` removal, and every `)` insertion where an argument ends (before a comma, a `)`
  or the end), and re-checks after each. An edit "works" when no bracket, shape or count fault is
  left (an unrelated typo can remain). The window then:
  - **one edit works**: the first line is the fix, e.g. *"Remove the ) after …start, TAKE(moves,
    5) (character 101)."*, then a green panel with the spot and what the changed call reads after
    the edit (`REDUCE(start, TAKE(moves, 5), step)`), then the other problems, with those the fix
    clears listed under "Caused by this )";
  - **several work**: they're listed, likeliest first (an insertion at the end of a line or the
    formula, then nearest the first fault), each with its preview: "Check which one you meant";
  - **none works**: the edits that leave fewest faults, if any reduce them, or "No single
    bracket edit fixes this."
- **Instant.** One `)` edit moves the bracket balance by one, so the search runs only when the
  formula is off by exactly one: removals for an extra `)`, insertions for a missing one. A
  formula whose brackets balance (an argument-count fault alone) gets no panel. Only edits up to
  the first fault are tried (text before it reads the same after any later edit), nearest first;
  each re-check reuses the tokens, skips name suggestions and stops once it can't beat the best
  edit so far. The search stops at 150 ms; when it does, the window says so, doesn't lead with
  the fix and doesn't fold problems under it (uniqueness isn't proven). Above 12,000 characters
  it isn't tried. On the Mac (net9, M4), a 5,000-character LET: 8–26 ms with a stray `)`; about
  150 ms with a `)` missing at the end (the end-of-line insertion is tried first, so it's found
  even when the clock cuts the search). In Excel in the VM the window was up 0.15–0.7 s after the
  key, mid-edit.
- **Library counts** trust the header's flags and say so ("(by its library header)"): a
  workbook whose own LAMBDA shares a library name, or an out-of-date header, can be wrong.
- **Known limit.** The search finds *valid* edits, not intended ones. In stall 8 (Tim deleted
  `TAKE(` and left its `)`), removing the `)` after `EXPLODE(G142` is valid, because EXPLODE takes
  a chunk size; Tim restored `TAKE(` instead. The preview shows `EXPLODE(G142,$I$140)`, which is
  why every fix shows what the call reads after it.

Screenshots (VM, Excel 365, mid-edit, Ctrl+Shift+K): `assets/0014-cf2-01-tim-reduce.png` (one
fix), `assets/0014-cf2-03-stall1-several.png` (two valid edits), `assets/0014-cf2-02-stall7.png`,
`assets/0014-cf2-04-argcount.png`.

## How it's built

- **`FormulaChecker`** (pure, unit-tested): one left-to-right pass over
  `FormulaParser.Tokenize`'s tokens with a stack of open calls. It needs no parse tree, so it works
  on the broken formulas Excel refuses, and it is linear in the formula's length (the 3,000-character
  LET test checks in milliseconds; 5,000 nested brackets don't recurse). Scope comes from
  the same stack: a LET name is visible after its comma to the end of its LET, a LAMBDA parameter
  to the end of its LAMBDA. When the stack empties and a comma or `)` follows, the report names the
  `)` that emptied it. Nearest names use edit distance (a swap counts once): 1 edit for names up
  to 4 characters, 2 up to 8, 3 beyond; failing that, a known name of 4+ letters inside the
  unknown one.
- **Native function list** (`ExcelFunctions`): Microsoft's alphabetical list (532 names, plus
  `ANCHORARRAY`, `SINGLE` and a few undocumented ones such as `DATEDIF`). `_xlfn.`, `_xlws.` and
  `_xlpm.` prefixes are ignored.
- **Reading the formula being edited.** During cell edit Excel runs no macros, ribbon commands or
  OnKey shortcuts, and the in-cell editor exposes no text through UI Automation or `WM_GETTEXT`
  (both tried in the VM). So a thread keyboard hook on Excel's main thread watches for
  Ctrl+Shift+K; when Excel is editing (`xlGetFmlaInfo` through `XlCall.PenHelper`, the call
  ExcelDna.IntelliSense uses) it reads the text, swallows the key and hands the text to the
  window thread. Outside edit mode the key goes through to the OnKey command.
- **Workbook names.** Read through COM when the active cell is checked. Mid-edit Excel refuses COM
  calls, so a snapshot is kept: a timer queues a macro every 5 s that re-reads names, sheets,
  tables and columns only when the workbook's shape (names, sheets, table counts) changed, or
  once a minute. It does nothing while Excel is editing or busy.
- **Library names** come from the catalogue the argument hints use (spec 0013), filled from the
  popup's library cache when the switch starts.
- **Lifecycle.** The switch takes effect at once from the settings window: on, it registers the
  OnKey shortcut, installs the hook and starts the timer; off, it undoes all three. The ribbon
  button shows only with the switch on.

## Defaults Tim can reverse

- **Ctrl+Shift+K**, fixed (not in settings). Excel doesn't use it.
- **One window, focus taken.** It opens activated so Esc closes it; Excel gets focus back after.
- **Brackets listed before names**, then by position.
- **Unused LET names are shown as notes** (amber), not errors.
- **No fixes applied.** The window shows the nearest name and the bracket edit; it doesn't
  rewrite the formula.
- **Argument counts only where certain**; library LAMBDAs trust their header flags.
- **Unknown names are double-checked with Excel** before they're reported for a cell.
- **Sheets and tables are checked only when the snapshot has them** (a workbook was read).
- **Positions are 1-based characters**, counting the `=`.

## Not in scope (ideas for later)

- A stray comma inside a call (the repair search tries brackets only).
- Moving Excel's cursor to the problem.
- Local-language formulas (`;` separators): Tim's Excel is English.
