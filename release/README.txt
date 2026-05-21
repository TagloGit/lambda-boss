Lambda Boss
===========

A single-file Excel add-in that gives you access to a curated library of
LAMBDA functions plus a popup UI for inserting and editing them. Built on
ExcelDNA and .NET Framework 4.8 — no runtime install required.

What's in this zip
------------------

  lambda-boss64.xll   The add-in itself. Code-signed.
  README.txt          This file.
  unblock.cmd         Optional helper for clearing the "downloaded from
                      the internet" mark Windows applies to USB-copied
                      files.

Requirements
------------

  - 64-bit Excel (Microsoft 365 or Excel 2019+).
  - Windows 10 / 11. (.NET Framework 4.8 is part of Windows — no extra
    runtime install needed.)

Load instructions
-----------------

There are three ways to load the add-in. Pick whichever fits your setup.

1. AddIns folder + Excel Add-Ins dialog  (recommended for daily use)

   a. Copy lambda-boss64.xll into:
        %APPDATA%\Microsoft\AddIns\
      You can paste that into the Windows Explorer address bar.
   b. Open Excel.
   c. File -> Options -> Add-Ins.
   d. At the bottom: Manage: Excel Add-Ins -> Go.
   e. Tick "Lambda Boss" in the list.

   Excel will load it automatically on every start from now on.

2. Any folder + Excel Add-Ins dialog  (portable / tournament use)

   Same as (1) but copy the XLL anywhere you like (e.g. a folder on
   your USB stick, or Documents\Lambda Boss\). In step (d), click Browse
   and pick the XLL from that folder. Excel remembers the path.

3. Double-click  (single session)

   Just double-click lambda-boss64.xll. Excel will launch with Lambda
   Boss enabled for that session only. Quickest if you only need it
   once.

If Excel says "this add-in is blocked"
--------------------------------------

Windows tags files copied from the internet (or a USB stick) with a
"Zone.Identifier" mark that some Excel security settings will refuse to
load. To clear it:

  Option A (one-shot):
    Right-click lambda-boss64.xll -> Properties -> tick "Unblock" near
    the bottom -> OK.

  Option B (all files in this folder at once):
    Double-click unblock.cmd. It runs:
      powershell -Command "Get-ChildItem -Recurse | Unblock-File"

After unblocking, retry the load.

Source code
-----------

  https://github.com/TagloGit/lambda-boss
