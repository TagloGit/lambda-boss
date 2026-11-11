# 0017: The popup opens on its commands; keys move off K and L

Issue #458. Lead record; not for Tim's review. Tim approved the change on 11 Nov (excel-esports#20),
for the MEWC finals on 1–2 Dec; the layout is fixed by the 20 Nov freeze so he has two weeks of
muscle memory.

## Why

The popup was built to load one library at a time. Tim now always imports every library with no
prefix and works through the commands (/State, Edit Lambda, LET to LAMBDA). Every keystroke counts
under the clock, so the commands come first and library loading goes behind one of them. His
competition macros (CompetitionKit.xlam, excel-esports#77) bind Ctrl(+Shift)+K and L for alignment
and sizing, so Lambda Boss stops using those keys.

## What changes

### The popup opens in Commands mode

- Typing filters the commands; no `/` needed. A leading `/` still works, so the old habit is safe.
- Enter runs the selected command; Esc closes.
- The order is fixed, never most-recently-used. Ties in the filter keep that order (they used to
  fall back to alphabetical).

| # | Command | Why here |
|---|---|---|
| 1 | Import All Lambdas (no prefix) | Hotkey then Enter imports everything bare |
| 2 | State | The most-used command |
| 3 | Debug | Tim toggles debug constantly since #457 |
| 4 | Edit Lambda | |
| 5 | LET to LAMBDA | |
| 6 | Gather | |
| 7 | Refactor | |
| 8 | Unnest | |
| 9 | Library | Library loading and lambda search |
| 10 | Import All Lambdas | The prefixed import, next to Library |
| 11 | Settings | |

State and Debug show only with the `state-any-form` Labs switch, as State did. The order lives in
`UI/PopupCommands.cs`; the window sorts its registry into it.

### Library loading goes behind `Library`

`Library` opens the old library view: Tab switches between libraries and lambda search, Enter
on a library's exact name or prefix still loads it (#449), and the prefix prompt works as before.
Esc there returns to the commands (Esc in the prefix prompt still just closes the prompt). The
stale-library banner and Ctrl+R (#452/#454) work in every mode. The mode bar reads
"Commands · Libraries · Search" and its labels still switch mode on click.

### Setting: Popup opens on Commands | Libraries

`Settings.PopupOpensOn`, in the settings window, default Commands. Libraries gives the old popup:
it opens on the library list, `/` enters the commands, the first Esc clears the `/`, and the mode
bar and status line read as before. Two things differ from the old popup in either setting: the
command order above, and "Load Library" is now called "Library".

### Keys

| Action | Before | After |
|---|---|---|
| Open the popup | Ctrl+Shift+L | Ctrl+Shift+J |
| Check Formula, in a cell or while editing | Ctrl+Shift+K | Ctrl+Shift+C |

- `Settings.KeyboardShortcut` defaults to `^+J`. A settings file still on `^+L` moves to `^+J`
  once (`PopupShortcutMoved` records it, so choosing `^+L` again afterwards sticks).
- `Settings.CheckFormulaShortcut` (new) defaults to `^+C`. It drives both the OnKey command and the
  edit-mode keyboard hook. `ShortcutKey` reads the OnKey form (`^` Ctrl, `+` Shift, `%` Alt, then a
  letter, digit or F1–F12) into the hook's virtual key and modifiers; anything else falls back to
  the default and says so in the log. The hook now matches the modifiers exactly.
- Ctrl+Shift+J and Ctrl+Shift+C have no built-in Excel action, so nothing of Excel's is lost. With
  Lambda Boss off K and L, Ctrl+Shift+L is Excel's filter toggle again unless the kit takes it.

### One-step debug toggle

- **`Debug`** ("Toggle state debug on the active cell"): runs `StateDebug.Toggle` and saves, the
  same as ticking the /State dialog's Debug box and pressing Save, with no dialog. On turns the
  fold into its SCAN twin inside the debug wrapper; off gives back the exact text (#447).
- **Alt+D in the /State dialog** toggles Debug and saves in one press. Alt+D is the box's access key
  (`_Debug`, underlined while Alt is held), so it is discoverable the Windows way; no other control
  in the dialog uses D, and Alt+letter can't be typed into the variable boxes by accident.

## Keystrokes for Tim's common actions

| Action | Before | After |
|---|---|---|
| Import everything bare | Ctrl+Shift+L, `/` and filter to it, Enter | Ctrl+Shift+J, Enter |
| /State | Ctrl+Shift+L, `/sta`, Enter | Ctrl+Shift+J, `sta`, Enter |
| Debug on/off | /State, click Debug, Enter | Ctrl+Shift+J, `deb`, Enter; or Alt+D in /State |
| Load one library | Ctrl+Shift+L, name, Enter, Enter | Ctrl+Shift+J, `lib`, Enter, name, Enter, Enter |

## Tests

`CommandsFirstTests`: the fixed order and the first command; filtering with and without `/`
(`sta` → State, `deb` → Debug, `lib` → Library); ties keep the registry order; the opening mode per
setting and Esc's job in each mode (`PopupNavigation`); the setting's round trip; the one-time key
move; `ShortcutKey` parsing. Proved in real Excel in the VM: see the PR.
