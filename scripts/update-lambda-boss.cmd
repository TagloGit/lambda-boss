@echo off
REM One-command Lambda Boss update. Run with Excel closed.
REM   update-lambda-boss.cmd            installs dev-latest
REM   update-lambda-boss.cmd --stable   installs the latest release
REM This file can live anywhere: without its .ps1 beside it, it fetches a copy.
setlocal
set "SCRIPT=%~dp0update-lambda-boss.ps1"
set "FETCHED="
if not exist "%SCRIPT%" (
    set "SCRIPT=%TEMP%\update-lambda-boss-%RANDOM%.ps1"
    set "FETCHED=1"
)
if defined FETCHED (
    curl.exe -fsSL -o "%SCRIPT%" "https://raw.githubusercontent.com/TagloGit/lambda-boss/develop/scripts/update-lambda-boss.ps1"
    if errorlevel 1 (
        echo ERROR: Couldn't download update-lambda-boss.ps1.
        set "RESULT=1"
        goto done
    )
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RESULT=%ERRORLEVEL%"
if defined FETCHED del "%SCRIPT%"
:done
REM Keep the window open after a double-click. Automation sets LB_NO_PAUSE.
if defined LB_NO_PAUSE exit /b %RESULT%
echo %cmdcmdline% | findstr /i /c:"%~nx0" >nul
if not errorlevel 1 pause <con
exit /b %RESULT%
