# 0016: The library a workbook gets is the current one, and an old copy shows

Issue #452. Lead record; not for Tim's review.

## What happened

On 11 Nov Tim's R4 drill workbook held all eight libraries as develop had them between 9 Nov 19:44
(bdc9cd0) and 10 Nov 18:22 (0cb3efc, #444). His `SplitState` showed one error across every
column, because #442's fix wasn't in his SetVars. He had run `update-lambda-boss.cmd` and his
channel is Labs. Nothing on screen said the workbook's copy was old: the popup said "✓ loaded".

## How a stale copy arises (what the code allowed)

1. **`/State` and Import All read the disk cache** (60-minute TTL) instead of fetching. Only the
   popup's own load invalidated first.
2. **When GitHub can't be listed, every load falls back silently**: to the disk cache within its
   TTL, else to the copy built into the XLL, whatever its age.
3. **GitHub's unauthenticated limit is 60 API calls an hour per public IP**, shared by every machine
   behind it (Tim's PC, the Mac and its VM). A refresh spent one call per library, about 10. On
   11 Nov at 15:06 UTC the Mac's IP had used 55 of 60.
4. **A workbook keeps whatever it was given.** A file saved with an old library, or a library
   loaded from an old cache or an older XLL's built-in copy, stays old until reloaded.

Tim's PC log (`%LOCALAPPDATA%\LambdaBoss\logs\LambdaBoss.log`, 11 Nov) would show which of these it
was. The snapshot fits develop on GitHub during that window, a cache saved then, or the library built
into the 10 Nov 15:03 build (e375d7c, #436). A current XLL fetching while online can't produce it, so
an older XLL still loaded (or a cache) together with 2 and 3 is the likely route (inferred).

## What changes

- **A load always fetches** when GitHub answers: popup, `/State` and Import All alike. The cache
  serves only the popup's list.
- **Offline or rate-limited, the newer copy wins**: the disk cache (any age) only if it was saved
  after the XLL's library was built (`LibraryBuiltUtc`, stamped at build), else the built-in copy.
  The popup's status line says which copy and why ("GitHub's hourly limit … is used up until
  16:05 UTC: using the library built into the add-in (11 Nov 08:22 UTC)").
- **One API call per listing**: a recursive git tree answers every library and file listing, with
  the contents API as fallback. A refresh costs 1 call instead of about 10.
- **The update script clears the disk cache**, so a new build never starts from an old one's copy.
- **The popup marks an out-of-date copy**: when it opens, each library the workbook holds is
  compared, name by name, with the add-in's copy under the workbook's prefix (whitespace and letter
  case outside strings ignored). A differing or missing name makes the row read
  "⚠ out of date (n of m)" and shows one banner above the list; **Ctrl+R or a click reloads** every
  out-of-date library with the prefix the workbook already uses.

## Where the warning lives, and why the popup

The popup is where libraries are loaded, it already scans the workbook's names on open, and the
fix (a reload) is one key there. The comparison runs on the popup's thread after its data loads
(no network, no workbook-open cost), and offline it compares with the built-in or cached copy.
`/State` doesn't warn: it loads Loops itself only when the workbook has none, and now that load
fetches fresh.

## Popup race (lead-excel-strip, 11 Nov: `loops` + Enter loaded Math)

The popup's data arrives a few seconds after the first open on a new Excel. On arrival it called
`ResetAndShow`, which **cleared the search box**. Text typed during the load was cut to a
fragment, and Enter loaded the top search hit of the fragment. Now arrival refilters around what's
typed, and an Enter pressed before the data arrives waits for it ("Enter will load when they
arrive").
