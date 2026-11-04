Mantle Place for AutoCAD
========================

The Mantle Place plugin for AutoCAD and Civil 3D 2025, 2026 and 2027. AutoCAD and Civil 3D are one
plugin: Civil 3D's part loads only when Civil 3D is running.

This release is the plugin's shell. It loads and says what it is; it does not import a Mantle
Place bundle yet. The command MANTLEPLACEABOUT prints the version that loaded and whether the
Civil 3D part is on.

This package is one folder, MantlePlaceAutoCad.bundle. That is an Autodesk application plugin
bundle, so the same package serves the Autodesk storefront and a hand install. Inside it: this
file, Install.cmd, Deploy-MantlePlaceAutoCad.ps1, PackageContents.xml (AutoCAD reads it to pick
the right build for your year; you do not need to) and the "Contents" folder that holds the plugin.

Contents holds two builds. AutoCAD and Civil 3D 2025 and 2026 load the "net8" one and 2027 loads
the "net10" one; PackageContents.xml says which, so you copy the whole folder and never choose.

Use ONE way in. If Mantle Place for AutoCAD is already installed from the Autodesk storefront, or
sits in an Autodesk "ApplicationPlugins" folder, remove that install before the steps below:
AutoCAD would load the plugin twice. Install.cmd checks for it and stops, naming the folder.

The Revit plugin has its own package and its own folder. The two sit side by side without
conflict.


-------------------------------------------------------------------------------
INSTALL -- copy one folder
-------------------------------------------------------------------------------

Nothing here needs administrator rights. The plugin installs for your Windows account only, and
you do not need to install .NET: AutoCAD brings its own, and this plugin loads inside it.

1. Close AutoCAD and Civil 3D. Their plugin files are locked while they are open.

2. Open Windows Explorer, click the address bar, paste this and press Enter:

       %APPDATA%\Autodesk\ApplicationPlugins

   If the folder is not there, make it, spelled exactly like that.

3. Copy this whole package folder, MantlePlaceAutoCad.bundle, into it. The folder keeps its
   name. You may leave this README, Install.cmd and the script behind; AutoCAD uses only
   PackageContents.xml and Contents.

4. Start AutoCAD or Civil 3D. If it asks whether to load the plugin, answer Always Load. Type
   MANTLEPLACEABOUT: the command line names the version.

If nothing happens, check that PackageContents.xml sits directly inside
%APPDATA%\Autodesk\ApplicationPlugins\MantlePlaceAutoCad.bundle, not in a sub-folder.


-------------------------------------------------------------------------------
INSTALL -- or let the script do it
-------------------------------------------------------------------------------

Double-click Install.cmd. It does exactly what the steps above do and prints what it wrote.

Windows may ask whether you want to run it, because it came from the internet. That prompt is
expected; click Run.

If your workplace blocks scripts, the script will refuse to start and say so. That is not a
problem you need to solve -- the copy above is the whole install, and it always works.


-------------------------------------------------------------------------------
UNINSTALL
-------------------------------------------------------------------------------

Close AutoCAD and Civil 3D, then delete the folder
%APPDATA%\Autodesk\ApplicationPlugins\MantlePlaceAutoCad.bundle. If you installed from the Autodesk
storefront, uninstall it there instead.
