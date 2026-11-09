# 0013: Argument hints for library LAMBDAs, and Help out of the formulas

Record of #382, and the Help part of #381 (Tim's direction of 6 Nov 2026 on #382). The lead
wrote this as its record; Tim doesn't review it. Choices Tim can reverse are listed at the end.

## Why

Excel shows argument hints for native functions but not for LAMBDAs held in defined names.
Under the clock Tim stalls, or puts an argument in the wrong slot: in rep R1 (5 Nov) he typed
`TLOOKUP(jRain,G98)`, the key in the second slot, and lost 22 s fixing it (excel-esports#110).
The fix is to show the signature and the current argument's meaning while he types, the way
Excel does for `XLOOKUP(`.

The Help block inside every library formula (`IF(Help?, TEXTSPLIT(…), result)`) was the old
way to see a signature: call `=FN()` and read the table. It also fills Edit Lambda and the Name
Manager with help text (#381). Once hints exist, the help moves out of the formulas into each
file's header, and the hints read it from there (Tim, 6 Nov).

## What Tim sees

The hints are on by default (out of Labs on 9 Nov, #422: "Argument hints look awesome!!! That can come out of Labs." — Tim). The settings window (the ribbon's channel button) has a checkbox, **Argument hints for library LAMBDAs**, to turn them off. With them on:

- **Choosing the name.** In Excel's autocomplete list, when a library LAMBDA is highlighted, a
  tooltip beside the list shows what it returns and what it does.
- **Typing the arguments.** After `=TLOOKUP(` a tooltip under the cell shows:

  ```
  TLOOKUP(Table, [Item], [LookupCol], [HeaderName], [If_not_found])    <- current argument in bold
  Returns: the value under HeaderName for each Item (row mode: the matched row)
  Looks up a value from a header-row table by matching a row key ...
  Table: Full table including its header row (e.g. tblData[#All]). ...
  ```

  The bold argument follows the cursor as commas are typed. Optional arguments are in square
  brackets, as Excel shows them for native functions.
- **Excel already shows a LAMBDA's parameter names** (found in the VM, 9 Nov): typing
  `=TLOOKUP(` shows Excel's own one-line tip, `TLOOKUP([Table], [Item], …)`, with the current
  argument in bold, and it shows it with or without the hints. The hint's own signature line
  sits just under it and is mostly hidden by it. What the hints add is everything under that
  line: what the function returns, what it does, and what the current argument means.
  Excel's tip shows every parameter in brackets, because the Help pattern made them all
  optional; the hints' metadata knows which are required.
- The hints work for bare names (`TLOOKUP`) and prefixed ones (`lookup.TLOOKUP`). Proven in
  the cell; ExcelDna.IntelliSense also follows the formula bar, not yet driven in the VM.
- Native functions are untouched: their hints are Excel's own.
- With the checkbox off nothing changes and nothing is installed.

## How it's built

- **Engine: [ExcelDna.IntelliSense](https://github.com/Excel-DNA/IntelliSense)** (MIT, the
  Excel-DNA project's own on-sheet IntelliSense, NuGet 1.9.0, packed into the XLL). It watches
  Excel's formula editing through UI Automation on its own thread, parses the function name and
  argument index from the text, and draws the tooltips. It matches names case-insensitively and
  accepts dotted names, so prefixed library names work.
- **It already supports names it isn't told are UDFs.** Its XML provider reads
  `<xll name>.intellisense.xml` beside every loaded Excel-DNA add-in, and the display looks a
  typed name up in that list without asking whether it's a UDF. So no fork is needed: Lambda
  Boss writes `lambda-boss64.intellisense.xml` beside its own XLL and asks the server to
  refresh. Its provider classes are internal and closed to extension (its own comment says so),
  which rules out an in-memory provider without forking; the file route is the supported one.
- **Co-operative.** Several Excel-DNA add-ins can each carry the IntelliSense server; the
  newest version runs and reads every add-in's XML file. If another add-in's server is active,
  Lambda Boss's file is still read and `IntelliSenseServer.Refresh()` reaches that server.
- **What goes in the file.** Every LAMBDA the popup knows (every library of every enabled
  source, from the same cache the popup uses) under its bare name and under its library's
  default prefix, plus any prefix chosen when a library is loaded this session. Names that
  aren't defined in the workbook do no harm: Excel's autocomplete never offers them, and typing
  one by hand gives `#NAME?` anyway.
- **When it's refreshed.** At startup (in the background, from the disk cache or GitHub, as the
  popup would), each time the popup's library list refreshes, and when a library is loaded with
  a new prefix. The file is written and `Refresh()` queued as a macro, never on the typing path.
- **The file stays on disk** between sessions, so a server started by another add-in before
  Lambda Boss's `AutoOpen` still finds it. It's deleted when the hints are turned off. If the
  XLL's folder isn't writable, the hints stay off and the log says why.
- **First session beside another IntelliSense add-in.** If another add-in's server starts first, it
  looks for the file when Lambda Boss's XLL loads, before `AutoOpen` has written it, so the very
  first session shows no hints. From the second session on the file is already there.
- **Custom prefixes are per session.** A prefix chosen at load is in the file until the next
  session's first write; loading the library again brings it back.
- **Lifecycle.** `IntelliSenseServer.Install()` runs in `AutoOpen` when the hints are on, or
  when they are turned on in the settings window; `Uninstall()` runs in `AutoClose` and
  when they are turned off.

## Where the metadata lives

In each `.lambda` file's header comment, which the loader already strips. The formula carries
none of it.

```
/*  FUNCTION NAME:      TLOOKUP
    DESCRIPTION:*//**Looks up a value from a header-row table ...*/
/*  RETURNS:            the value under HeaderName for each Item (row mode: the matched row)
    PARAMETERS:
        Table           (Required) Full table including its header row (e.g. tblData[#All]). ...
        Item            (Optional) Value to find in LookupCol to identify the row. ...
    EXAMPLE:            =TLOOKUP(tblData[#All], "Tim", tblData[Person], "Age") -> 42
    NOTE:               ...
    REVISIONS:          Date        Developer   Description
                        ...
*/
```

- `DESCRIPTION` is unchanged (the popup already reads it).
- `RETURNS` is one line: the output type first, then what it holds. It makes the fixed-output
  convention (excel-esports `docs/lambda-families/positions.md`) visible at the moment of use.
- `PARAMETERS` has one line per LAMBDA parameter, in the LAMBDA's order: the name, then
  `(Required)` or `(Optional)`, then the description. The names and order come from the LAMBDA
  itself; the header gives the text. `LambdaFormatTests` checks every parameter has a line, in
  order, with a required/optional marker, and that `RETURNS` is filled.
- `EXAMPLE` and `NOTE` keep the rest of what the Help table held. They aren't shown in hints.
- `LambdaDocParser` reads it; `LambdaInfo.Doc` carries it for the popup and the hints.
- The first pass (with the hints) generated the header metadata from each file's Help table,
  and wrote `RETURNS` by hand per function. The second pass removes the Help tables.

## Performance

No lag while typing: the UI Automation work and the tooltip drawing run on
ExcelDna.IntelliSense's own thread, the lookup is a dictionary hit, and Lambda Boss does nothing
on the typing path (it only writes the file on refreshes). Measured in the VM (9 Nov): a
158-key formula sent key by key, three runs each, took 60.4 ms/key with the hints on and
61.9 ms/key with them off, and every formula arrived exact.

## Help out of the formulas (second PR)

Tim's direction (6 Nov), done after the hints were proven:

- Every `.lambda` file loses `Help?, ISOMITTED(…)` and the `IF(Help?, TEXTSPLIT(…), result)`
  block; the LET ends in `result`. The MAZEHELP companion goes (its table is MAZE's metadata).
- **`FN(TRUE)` / `=FN()` call-time help goes too.** The hints cover what it showed (signature,
  parameters, description, output); the example stays in the header. The zero-argument getters
  that used `ShowHelp` to flip into help (`DEFAULTARROWS()`, `ALPHABET()` and others) lose that
  parameter. The square brackets on parameters stay as they are, so no call changes meaning:
  required-ness lives in the metadata.
- The `help text` cases in `*.tests.yaml` go.
- `create-lambda` (the template) and docs spec 0002 describe the new header and no Help block.
  `LambdaFormatTests` stops requiring a Help table and forbids one.
- **Parser and Edit Lambda.** The add-in has no Help-specific code: `TransformHelpPattern` was
  removed under docs spec 0002, and Edit Lambda shows whatever the formula holds, so removing
  the block from the files is what clears Edit Lambda. The parsers keep accepting `?` in names,
  because library and user LETs use names like `keyDropped?`. Code comments that cite `Help?`
  as an example of such a name stay: it's still a valid user name.

## Defaults Tim can reverse

- **The engine is ExcelDna.IntelliSense, not our own window.** Its tooltip looks like Excel's
  own, not like Lambda Boss's popups.
- **The hints read the library, not the workbook.** A LAMBDA edited by hand in the Name Manager
  still shows its library hint.
- **Names under both bare and default-prefix forms**, plus prefixes chosen at load.
- **The tooltip shows the whole parameter description**, wrapped, not just its first sentence.
  Long descriptions take three or four lines.
- **The function description shows in the argument tooltip too** (ExcelDna.IntelliSense always
  shows it), under `Returns:`.
- **Examples aren't shown in hints.**
- **`FN(TRUE)` help is gone** rather than kept beside the hints.
- **The checkbox takes effect at once** in the settings window; no restart.

## Not in scope

- The array-lift dispatcher in Edit Lambda (#381's other part).
- Hints for user-written LAMBDAs that aren't in a library.
- The Name Manager comment Lambda Boss stamps (`[LambdaBoss] repo|library|prefix`), which
  Excel shows as the name's description in its own autocomplete tooltip.
