@echo off
setlocal

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
pause
endlocal
