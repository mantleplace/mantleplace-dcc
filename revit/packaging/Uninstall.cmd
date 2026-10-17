@echo off
setlocal

rem  Mantle Place for Revit -- one-step uninstaller.
rem
rem  The mirror of Install.cmd, and wrapped for the same reason: a .ps1 in a downloaded zip is not
rem  something a curator can run (Restricted execution policy, Mark-of-the-Web), and
rem  -ExecutionPolicy Bypass on the command line clears both. A Group Policy execution policy
rem  overrides it, and for that case the manual steps in README.txt, "UNINSTALL", are the fallback.
rem
rem  powershell.exe, not pwsh.exe -- Windows PowerShell 5.1 ships with Windows, PowerShell 7 does not.

echo Uninstalling Mantle Place for Revit...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-MantlePlaceRevit.ps1"

if errorlevel 1 (
    echo.
    echo ---------------------------------------------------------------------
    echo The uninstaller did not finish.
    echo.
    echo If the message above is about scripts being disabled on this system,
    echo your workplace blocks scripts and nothing here can change that.
    echo Uninstall by hand instead -- README.txt, "UNINSTALL". It takes a
    echo minute and it always works.
    echo ---------------------------------------------------------------------
) else (
    echo.
    echo Done.
)

echo.
rem  Without this the window vanishes the instant it finishes and the curator sees nothing at all --
rem  neither what was removed nor the reason it failed.
pause
endlocal
