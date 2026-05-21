@echo off
REM Clears the "downloaded from the internet" Zone.Identifier mark
REM from every file in this folder (and any subfolders).
REM Run this if Excel refuses to load the XLL after a USB copy.
powershell -NoProfile -Command "Get-ChildItem -Path '%~dp0' -Recurse | Unblock-File"
echo.
echo Done. Files in this folder have been unblocked.
echo.
pause
