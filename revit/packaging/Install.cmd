@echo off
setlocal

echo Installing Mantle Place for Revit...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-MantlePlaceRevit.ps1" -PayloadDirectory "%~dp0Contents"

if errorlevel 1 (
    echo.
    echo ---------------------------------------------------------------------
    echo The installer did not finish.
    echo.
    echo If the message above is about scripts being disabled on this system,
    echo your workplace blocks scripts and nothing here can change that.
    echo Install by hand instead -- README.txt, "INSTALL - copy four things
    echo into a folder". It takes a minute and it always works.
    echo ---------------------------------------------------------------------
) else (
    echo.
    echo Done. Start Revit and look for the "Mantle Place" tab.
)

echo.
pause
endlocal
