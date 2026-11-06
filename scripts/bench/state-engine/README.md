# State-engine hot-loop benchmark

Times a REDUCE state engine (1,000 and 10,000 steps, 2 and 5 values) in real Excel with different
state carriers: SetVars/GetVar, HSTACK + INDEX, text and number packing. Method and results: spec
0011, appendix.

- `gen.py <out dir> <label>=<lambda-boss checkout> [...]` reads
  `lambdas/loops/{SetVars,SetVar,GetVar,NewState}.lambda` from each checkout and writes
  `lb386-names.tsv` (name, RefersTo) and `lb386-cases.tsv` (case id, formula) to `<out dir>`. Each
  label (letters and digits only: it goes into an Excel name) gets its own names
  (`GetVar_<label>`) and `a_<label>_*` cases, so versions of the library race in one workbook. The
  lean, lazy-Help and prototype variants in the appendix are in `gen.py`'s history (before #387).
- `bench.ps1` runs in Windows with Excel installed. It reads both TSVs from `\\Mac\serve` (the
  taglo-win shared folder; edit `$dir` for another location), defines the names in a fresh hidden
  workbook, and times `Range.Calculate` per case. Environment: `LB386_FILTER` (regex on case id),
  `LB386_REPS` (default 7), `LB386_OUT` (TSV file name).

On the Mac mini: run `gen.py` with `<out dir>` = taglo-pm `scripts/windows-vm/serve`, then
`vmps.py --user` a two-line wrapper that sets the environment and calls `\\Mac\serve\bench.ps1`
(copy it there first). Take the VM lock (`~/Repositories/wt/VM-LOCK`) first.
