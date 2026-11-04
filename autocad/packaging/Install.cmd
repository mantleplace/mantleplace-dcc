@echo off
setlocal

rem  Mantle Place for AutoCAD -- one-step installer.
rem
rem  This wrapper exists for one reason: a .ps1 in a downloaded zip is not something a curator can
rem  run. The default execution policy on Windows client is Restricted, so double-clicking a script
rem  opens Notepad; and Windows stamps Mark-of-the-Web onto files extracted from a download, so even
rem  a machine relaxed to RemoteSigned blocks it until somebody knows to right-click and Unblock.
rem  -ExecutionPolicy Bypass on the command line clears both.
rem
rem  It cannot clear a Group Policy execution policy, which overrides the command line. That case is
rem  not a bug and is not worth engineering around: the manual copy in README.txt is the whole
rem  install and always works. Say so and stop.
rem
rem  powershell.exe, not pwsh.exe -- Windows PowerShell 5.1 ships with Windows, PowerShell 7 does not.
rem  The payload is this folder itself: the script copies PackageContents.xml and Contents.

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
rem  Without this the window vanishes the instant it finishes and the curator sees nothing at all --
rem  neither the success line nor the reason it failed.
pause
endlocal
