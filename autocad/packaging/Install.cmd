@echo off
setlocal

echo Installing Mantle Place for AutoCAD...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-MantlePlaceAutoCad.ps1" -PayloadDirectory "%~dp0."

if errorlevel 1 (
    echo.
    echo ---------------------------------------------------------------------
    echo The installer did not finish.
    echo.
    echo If the message above is about scripts being disabled on this system,
    echo your workplace blocks scripts and nothing here can change that.
    echo Install by hand instead -- README.txt, "INSTALL - copy one folder".
    echo It takes a minute and it always works.
    echo ---------------------------------------------------------------------
) else (
    echo.
    echo Done. Start AutoCAD or Civil 3D and run MANTLEPLACEABOUT.
)

echo.
pause
endlocal
