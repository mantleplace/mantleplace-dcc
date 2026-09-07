Mantle Place for Revit
======================

Imports a Mantle Place bundle (MPB) into Revit: the toposurface from the bundle's points,
the site model linked from its IFC, imagery draped on the terrain, and the project placed at
the coordinates the bundle publishes.

Works with Revit 2025, 2026 and 2027.

One build serves all three. If you run more than one of them, install into each -- the files
are identical, and Revit reads a separate folder per year.

Revit 2024 and earlier are not supported and will not be: they run on .NET Framework 4.8,
where this plugin's dependencies are not part of the runtime.


-------------------------------------------------------------------------------
INSTALL -- copy four things into a folder
-------------------------------------------------------------------------------

Nothing here needs administrator rights. The plugin installs for your Windows account only,
and you do not need to install .NET: Revit brings its own, and this plugin loads inside it.

1. Close Revit. Its plugin files are locked while it is open.

2. Open Windows Explorer, click the address bar, paste this and press Enter:

       %APPDATA%\Autodesk\Revit\Addins

3. Open the folder for your Revit year -- 2025, 2026 or 2027. If it is not there, make it,
   spelled exactly like that.

4. Copy EVERYTHING inside this package's "Contents" folder into it. That is the .addin file
   and the .dll files beside it. Copy the .pdb files too if they are there; they cost
   nothing and they make a crash report useful.

5. Repeat steps 3 and 4 for each other Revit year you use.

6. Start Revit. A "Mantle Place" tab appears on the ribbon.

If the tab is missing, the files went into the wrong folder -- check that the .addin file
sits directly in the year folder, not in a sub-folder inside it.


-------------------------------------------------------------------------------
INSTALL -- or let the script do it
-------------------------------------------------------------------------------

Double-click Install.cmd. It does exactly what the steps above do, into every Revit year
folder at once, and prints what it wrote.

Windows may ask whether you want to run it, because it came from the internet. That prompt
is expected; click Run.

If your workplace blocks scripts, the script will refuse to start and say so. That is not a
problem you need to solve -- the copy above is the whole install, and it always works.


-------------------------------------------------------------------------------
UNINSTALL
-------------------------------------------------------------------------------

Close Revit, then delete MantlePlace.addin and the MantlePlace.* files from each year folder
under %APPDATA%\Autodesk\Revit\Addins. Nothing is written anywhere else except a cached copy
of bundles you download, under your local application data, which you can delete too.


-------------------------------------------------------------------------------
USING IT
-------------------------------------------------------------------------------

You need a bundle. Areas of interest up to 2 square kilometres are free -- a real bundle,
full quality, an account but no payment method:

  1. Create an account at https://mantle.place and draw an area of interest.
  2. Order a bundle for Revit.
  3. In Revit, either "Mantle Place > Account > Sign in" and then "Open vault" to pull it
     straight in, or download the zip and use "Mantle Place > Bundles > Import bundle zip".

Nothing needs configuring. Sign-in opens your normal browser; Revit never sees your password.

An import writes a log beside the bundle zip, named after it and ending
.mantleplace-import.log. Its first line names the plugin version. If you report a problem,
send that file -- it is the single most useful thing you can attach.

If an import is refused and the log does not explain enough, "Mantle Place > Bundles >
Probe terrain" measures what your project would give the importer and tries every placement,
rolling all of it back. It changes nothing in your project.


-------------------------------------------------------------------------------
LICENCE, AND WHAT A BUNDLE OBLIGES YOU TO DO
-------------------------------------------------------------------------------

This plugin is open source under Apache 2.0. Source, issues and releases:

  https://github.com/mantleplace/mantleplace-dcc

The DATA is separate. A bundle is built from licensed geospatial sources and some of those
licences carry obligations -- most often an attribution requirement -- that travel with the
data into your model. Which sources a bundle used and what it requires you to say are stated
per bundle by the platform, not by this package:

  https://mantle.place/licensing
  https://mantle.place/attributions
