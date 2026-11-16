# 0020: Example check on fills and spills

Record of TagloGit/excel-esports#80 (Tim, 16 Nov 2026, via the Excel director). The lead wrote it as
its record; Tim doesn't review it. Choices Tim can reverse are listed at the end.

## Why

Tim builds each level on the example row, then fills it to the games. On Gerrymandering (14 Nov)
three values from the example stayed in the general formula, and each passed the example *because*
it came from the example:

| Level | Slip | Cost |
|---|---|---|
| L4 | threshold typed as `0.09` (the example row's threshold, in H151) | 27 s |
| L5 | district `"A"` (the example row's district, in H192) | 6.8 min |
| L6 | `scores, 10/as`: 10 is the example district's area, stated only in the example text | 64 points, never caught |

The same habit cost 10–126 points in four other cases (excel-esports#43, `scripts/typed_values.py`).
Tim won't remember to press Check Formula at the end of a level, so the check fires on its own.

## Triggers

Excel's `SheetChange` event, read-only:

- **A formula written to more than one cell**: Ctrl+D, the fill handle, Ctrl+Enter on a selection,
  a paste of formulas, and Tim's Ctrl+Shift+F (a VBA macro in CompetitionKit.xlam, which writes
  the cells and so raises the same event). Each column of a multi-column write is checked as its
  own formula.
- **One formula whose result spills to more than one cell** (`HasSpill`, `SpillingToRange`).
- **Nothing else.** An ordinary one-cell edit costs one `HasFormula` read and returns. A multi-cell
  write with no formula in its first cell (Delete, a paste of values) returns at once.

The **family** of a filled column is the written rows holding the same formula (compared in R1C1)
as the column's first formula, extended upward over rows with that formula (gaps of up to four
blank rows), because the fill handle's write leaves out the row it was dragged from. The
**example row** is the family's top row; the other rows (up to 50) are what "varies" is judged
against. A spill's other rows are the rows it spills into below the formula. A family of one row
(a fill across, Ctrl+R) has nothing to compare and isn't checked.

## The matching rule

Literals come from the example cell's `Formula2`, through `FormulaParser.Tokenize`: numbers (with
a unary minus and a trailing `%`) and strings. A literal is flagged when it equals (numbers to 1e-9
relative; text trimmed, case-insensitive, as Excel's `=` compares) one of these, in this order:

1. **A cell on the example row, either side of the formula** (Tim, 16 Nov: "the inputs are on the
   same row as the formula, but they may be to the left or to the right"). The row is read
   outwards from the formula both ways until two empty columns in a row (one empty column, like
   Gerrymandering's gap between Answer and Map, doesn't end it). A cell counts only if:
   - its column **varies** across the family (some other row holds a different, non-empty
     value), so the literal can't be right for every row: a level column holding 4 on every row
     never matches, nor does an input box level with the first row with nothing below it;
   - the formula **doesn't read it**, alone or inside a range (the slip is a value typed *instead of* the cell; a formula
     that reads the cell and also compares it with a literal, like Cricket Darts'
     `throw="Bull"`, is using the input);
   - it isn't in any of the written columns (a fill of D:F excludes all three from each), nor in a
     column a written formula spills sideways into (that's its own output, not an input);
   Typed cells and computed cells (Tim's helper columns) both count, except that a small integer
   (|n| ≤ 10) matches typed cells only: computed cells are full of small counts.
2. **The first row of a multi-row range the formula reads that starts on the example row** (up to
   30 columns wide), where that column varies further down: a spilled MAP over `B2:B21` with the example's `"Rioja"` inside.
3. **A number the level's example text states and its rules text doesn't.** The level's text is
   the prose (text cells of 26+ characters, columns A–AD) above the formula, back to the
   `Level n` / `Bonus n` heading or 80 rows. Its example part starts at the first line beginning
   "Example"; the lines before it are the rules. This is how L6's `10` is caught ("E has a
   perimeter of 22 and an area of 10"), while `2` (in the rules: C = 2 × π × radius) isn't.

### Never flagged

- `0`, `1`, `-1`, `""` and TRUE/FALSE (TRUE/FALSE aren't literals the tokenizer yields as numbers).
- Unit and scale constants: 100, 1,000, 10,000, 100,000, 1,000,000 (percent, basis points).
- Literals in structural argument slots: an index, count, column number, format, mode or match
  type (`INDEX` 2nd+, `TAKE`/`DROP`/`CHOOSECOLS`, `SEQUENCE`, `ROUND` digits, `MOD` divisor,
  `XLOOKUP` 4th+ (the "not found" default), `MATCH`/`XMATCH` mode, `TEXT` format, `FILTER`'s
  if-empty, `IFERROR`'s fallback, `DATE`, `LAMBDA`, and the rest in
  `ExampleLiteralChecker.StructuralArgs`). `SWITCH`'s keys and results are the case's rules (an
  in-formula lookup table) and are never flagged.
- **Rules vocabulary**: a text literal of two or more characters whose words all appear, with the
  same capitals, in the workbook's prose outside example paragraphs ("Bull", "Azure", "Milk"; but
  not "Enacted plan" when the rules only say "the enacted plan"). One-letter text (`"A"`) is never
  vocabulary.
- A number that appears in both the level's rules and its example (the rules define it).

## What Tim sees

Three modes, chosen in Lambda Boss settings under the Labs switches ("Example check shows"). None
writes to the workbook.

| Mode | What it does | Interrupts? |
|---|---|---|
| `status` | Excel's status bar for 15 s ("Lambda Boss: a value from the example is left in the formula — 0.09 is H151's value on the example row"), and a drawn outline (a click-through overlay window, not cell formatting) round the written cells (amber) and the matching cell (blue) for 8 s. | No |
| `toast` **(default)** | A panel just right of the written cells, naming each value, the cell or text it matches, and an excerpt of the formula with the value highlighted. It never takes focus, so typing carries on; it closes itself after 20 s, or on ×. | Barely |
| `prompt` | The same content in a window over Excel that takes focus until Enter or Esc. | Yes |

**Check Formula** (its shortcut, Ctrl+Shift+C by default, or the ribbon button) on any cell of a
filled family or a spilling cell shows the same findings as "Value from the example" problems,
with the literal highlighted in the formula and the matching cell named. This needs the example
check's switch on.

## The Undo guarantee

Excel clears its undo stack when a macro or add-in writes to a workbook. The check only reads:
`HasFormula`, `FormulaR1C1`, `Formula2`, `Value2`, `HasSpill`, `SpillingToRange`, `UsedRange`,
and the window geometry for placing the panel. It runs inside the `SheetChange` handler (no
queued macro), so a fill can be undone with Ctrl+Z after a finding is shown. The `status` mode
sets `Application.StatusBar`, which is application state, not the workbook; it is put back by a
queued macro 15 s later only if the status bar still shows the check's text. The VM run below
confirms undo after a flagged fill in every mode.

## Cost

A finding needs: one `FormulaR1C1` read of the written column and one of the 60 rows above, one
`Value2` read of a block from 80 rows above the example row to the last compared row (≤ 200
columns), one `Formula2` read of the example row, and the workbook's prose once per workbook (400
rows × 30 columns per sheet, only when a text literal matches a cell).

Measured in real Excel (VM `taglo-win`, 16 Nov), after the start-up warm-up:

| Write | Time |
|---|---|
| 1,000-row fill with a finding | 15 ms |
| 1,000-row clean fill, including the one-off vocabulary read (17 ms) | 35 ms |
| 1,000-row spill | 2 ms |
| Gerrymandering fills (22 rows) | 2–23 ms |
| 262 fills and spills replayed over 9 played cases | mean 2–8 ms per file; one cold first check 115 ms |

Every finding is shown every time, even one shown a moment ago: a re-fill, an undo or a redo that
writes a flagged formula shows the panel again (Tim, 16 Nov video: "I think we should just default
to always showing it, even if it's already been seen"). Until then a finding shown in the last
minute wasn't shown again.

## Tuning (16 Nov, on Tim's files)

The rule was tuned on nine of Tim's worked files across vintages, then the shipped
`ExampleLiteralChecker` was run over the same files (exported cell values and formulas): every
vertical family of two or more rows "re-filled", every spilling cell "re-entered".

| Case (vintage) | Fills / spills | Flags (true / false) | Synthetic slips caught |
|---|---|---|---|
| Gerrymandering (2026 community) | 17 / 7 | 2 / 0 (L6's `10`, in the family and in a probe copy) | 18/20 |
| Monday Morning Quarterback (2026) | 25 / 9 | 0 / 0 | 24/25 |
| Grand Sumo (MEWC 2026 group stage) | 30 / 2 | 0 / 0 | 4/7 |
| Oachi in Zurich (DACH starter 2026) | 9 / 0 | 0 / 0 | 3/4 |
| A Good Walk Spoiled (2026 community) | 17 / 0 | 0 / 0 | 23/26 |
| Bushland Adventures (MEWC 2025) | 26 / 11 | 0 / 0 | 21/26 |
| Mama Africa's Kitchen (MEWC SA 2025) | 16 / 5 | 0 / 0 | 14/14 |
| Sommelier (RTLV 2024) | 36 / 0 | 0 / 0 | 23/23 |
| Cricket Darts (MEWC 2023) | 34 / 11 | 0 / 0 | 5/17 |

**Synthetic slips**: in each family, each same-row reference whose column varies was replaced by
its example-row value (Tim's slip, made on purpose) and the check run: 135 of 162 caught, inputs
to the left and to the right alike. The misses are by design: text that is the case's vocabulary
("Bull", "Azure", "Milk", "Grass"), small integers whose only match is a computed cell or a
structural slot, and long list strings. **Gerrymandering's three real slips: 3/3; its corrected
formulas: silent.** Before the rules-vocabulary and "a cell the formula reads" rules, Cricket
Darts raised five false flags (`"Bull"`, multipliers 2 and 3 in a helper grid) and MMQ one (`20`
matching a count in an unrelated table on the same row, now outside the row's block).

## Defaults Tim can reverse

- **Mode: `toast`.** The `ExampleCheckMode` setting (`status`, `toast`, `prompt`), in the settings window.
- **Only vertical families and spills are checked**, not fills across a row.
- **Two empty columns end the row's block**; one doesn't.
- **A cell the formula reads never matches.**
- **Small integers (|n| ≤ 10) match typed cells only.**
- **Powers of ten from 100 up are never flagged**; 10 can be.
- **Rules vocabulary is workbook-wide** and needs two or more characters.
- **Example text = from the first line starting "Example"** to the formula, within the level.
- **The panel closes after 20 s**; the status bar clears after 15 s; outlines last 8 s.
- **No fix button.** The panel names the cell to use; Tim edits the formula himself.

## Not in scope (ideas for later)

- Values the example computes inside the formula (a LET name's value at the example row), e.g.
  catching L6 through `areas` rather than the text. Evaluating LET steps needs `Evaluate`, which is
  limited to 255 characters.
- Horizontal fills, and inputs in rows above the formula (a header row of parameters).
- A fix button that swaps the literal for the cell.
- A value from a game row below the family's top row (a formula built on game 35, then moved to the
  top row and filled): only the top row is compared (Tim's question, 16 Nov video).
