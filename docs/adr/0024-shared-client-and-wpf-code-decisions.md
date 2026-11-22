---
name: adr-0024-shared-client-and-wpf-code-decisions
description: Decisions and reasons recorded from the code comments of the shared .NET client, platform API and WPF projects, covering auth, sign-in, secret storage, bundle cache and extraction, vault calls, Prepare re-join, realtime, and import window threading. Read before changing any of these areas.
status: accepted
---

# 24. The shared .NET client and WPF code: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the shared .NET code: the Client, PlatformApi and Wpf projects under `shared/src`. Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Machine records

**Announced orders, the feedback-asked record and the Prepare ledger are one file per machine, shared by all Revit versions, locked only by an exclusive open, with new bytes built before the write.** Side-by-side Revits then cannot act on the same order, and each order is announced once.
Source: shared/src/MantlePlace.Client/MachineRecordFile.cs, shared/src/MantlePlace.Client/AnnouncedOrderStore.cs, shared/src/MantlePlace.Client/FeedbackAskedStore.cs

**When a record cannot be written or is locked, announcing still announces and the feedback prompt asks nothing.** A duplicate notice is cheaper than a lost one, while a missed feedback question costs nothing and a repeated one is the nuisance the once-per-bundle rule prevents.
Source: shared/src/MantlePlace.Client/AnnouncedOrderStore.cs, shared/src/MantlePlace.Client/FeedbackAskedStore.cs

### Auth session

**There is one auth session per Revit process, not one per command.** Separate sessions would each hold their own access token, and signing out in one would leave the other authenticated.
Source: shared/src/MantlePlace.Client/AuthSession.cs

**StateChanged fires on thread-pool threads, and subscribers must marshal to an ExternalEvent or the dispatcher.** Touching a ribbon button from the callback is a main-thread violation that makes Revit terminate.
Source: shared/src/MantlePlace.Client/AuthSession.cs

**The sign-in cancellation source is not wrapped in a using.** Disposing it on exit while the field still pointed at it made the next CancelSignIn throw ObjectDisposedException on Revit's dispatcher and terminate the process.
Source: shared/src/MantlePlace.Client/AuthSession.cs

**The in-flight sign-in source is cleared by reference identity, not by a bare null.** A later flow's source is then never cleared, and a Cancel button that still has something to cancel is never disarmed.
Source: shared/src/MantlePlace.Client/AuthSession.cs

**A definitive refresh rejection discards the stored credential only if the store still holds the token that was presented; otherwise it retries once with the stored token.** The Unreal host shares the credential and may have rotated it, and discarding it would sign out both hosts.
Source: shared/src/MantlePlace.Client/AuthSession.cs

**The loopback bind-failure message does not blame another Mantle Place session, and sparse candidate ports are listed rather than shown as a range.** HTTP.SYS reports a Windows reserved port block as used by another process.
Source: shared/src/MantlePlace.Client/AuthSession.cs

### Loopback sign-in

**The loopback listener binds before the browser opens.** Opening the browser first and then finding no free port sends the curator to a page that redirects into nothing, with a hung tab as the only symptom.
Source: shared/src/MantlePlace.Client/LoopbackRedirectListener.cs

**Sign-in asks the OS for an ephemeral port instead of using a fixed list, with a probe-and-retry.** Windows Hyper-V reservations move across reboots and the hosts shared one finite list; HttpListener cannot bind port 0, so a probe finds the port first.
Source: shared/src/MantlePlace.Client/LoopbackRedirectListener.cs

**The listener prefix is the literal loopback IP that matches the redirect_uri exactly.** A localhost or wildcard prefix would either not match the redirect or require an administrator URL ACL.
Source: shared/src/MantlePlace.Client/LoopbackRedirectListener.cs

**The probe socket sets ExclusiveAddressUse before Start.** .NET's default SO_REUSEADDR lets another Windows socket share the port, so the probe would report a port it does not actually hold.
Source: shared/src/MantlePlace.Client/LoopbackRedirectListener.cs

**The host query parameter on the login URL is display-only and allowlisted.** It is never validated against a client registry, so it must not be signed or treated as a client id; it rides the base URL so the authorize parameter order is unchanged.
Source: shared/src/MantlePlace.Client/MantlePlaceEndpoints.cs

### Secret store

**The secret store's system-wide mutex name lives on the interface as a cross-host contract.** Both host plugins share one credential file, and the Unreal host takes the same named mutex through FSystemWideCriticalSection.
Source: shared/src/MantlePlace.Client/SecretStore.cs

**The null secret store refuses to persist rather than fall back to a less safe location.** A readable file would leave a bearer credential on disk while appearing to persist the session; memory-only auth is the correct degradation.
Source: shared/src/MantlePlace.Client/SecretStore.cs

**DPAPI protection is user-scoped, without CRYPTPROTECT_LOCAL_MACHINE, and is called by P/Invoke.** Accounts on a shared workstation then cannot decrypt each other's refresh tokens, and the ProtectedData package would break the no-NuGet rule.
Source: shared/src/MantlePlace.Client/SecretStore.cs

**The store lock is abandoned after five seconds and access proceeds unserialised.** A lock held that long belongs to a dead or hung process, and refusing sign-in would be a total failure for a rare race.
Source: shared/src/MantlePlace.Client/SecretStore.cs

**Crypt32 is imported with DllImport and ExactSpelling, not source-generated LibraryImport.** The generator needs AllowUnsafeBlocks across the whole assembly that handles the refresh token, and ExactSpelling is needed because the exports have no W or A suffix.
Source: shared/src/MantlePlace.Client/SecretStore.cs

### Bundle cache

**Inspect hashes the whole bundle with no size cap, and per-row listing paths must use InspectQuick.** Inspect runs once per import at the pre-import gate, and refreshes must not rehash large files.
Source: shared/src/MantlePlace.Client/BundleCache.cs

**The cache keeps one slot per host per order, and a zip fetched for another host or level is a miss.** A host's view holds only that host's files, and serving it across hosts would hide the files the other host reads.
Source: shared/src/MantlePlace.Client/BundleCache.cs

**A bundle's files live under one directory per order id, not per source.** Zips both named download.zip once shared a directory. Names are sanitised by HPS-30 (see docs/host-plugin-standard.md), and the four file names are a cross-host contract.
Source: shared/src/MantlePlace.Client/BundleCacheLayout.cs

**A zip that names no order is keyed by its full path, not its file name.** Every platform bundle was once called download.zip, so file names collide; HPS-30 sanitising applies the hash suffix as usual.
Source: shared/src/MantlePlace.Client/BundleCacheLayout.cs

**A bundle is filed by its filing key rather than its order id.** Bundles from before MPB 1.7.0 name their packaging job, and earlier imports stamped elements under that id, so filing by path would orphan re-imports.
Source: shared/src/MantlePlace.Client/LocalBundleArchive.cs

### Bundle log search

**The newest log is chosen by last-write time, not creation time.** A log is truncated at run start and appended throughout, so last write is what the curator means by the log.
Source: shared/src/MantlePlace.Client/BundleLogSearch.cs

**Log search makes two shallow passes and never recurses; unreadable paths count as absent.** The extracted directory holds tens of thousands of files that cannot be logs, and the walk runs on Revit's UI thread.
Source: shared/src/MantlePlace.Client/BundleLogSearch.cs

**Directory listings are materialised into a list rather than streamed.** An enumerator throws from MoveNext, which would move the try block onto every caller's loop where it gets forgotten.
Source: shared/src/MantlePlace.Client/BundleLogSearch.cs

### Bundle extraction and verification

**Extracted files have two lifetimes, decided by ImportStepKinds.LifetimeOf.** Imported toposurfaces are copied into the model, but linked DXF or IFC files are live path references, so extracting both to one deleted temp directory would silently break the links.
Source: shared/src/MantlePlace.Client/LocalBundleArchive.cs

**Every artifact the plan touches is hash-verified before any extraction; steps with no advertised hash are skipped.** Per-step verification would abort only after earlier steps had already created a toposolid and a link in the user's model.
Source: shared/src/MantlePlace.Client/LocalBundleArchive.cs

**PNG dimensions are probed by reading only the header prefix of the zip entry during planning.** Decoding or extracting a drape of tens of megabytes would turn a free refusal into an expensive one.
Source: shared/src/MantlePlace.Client/LocalBundleArchive.cs

**Extraction mirrors each entry's sub-path rather than flattening to the leaf name.** Two entries can share a file name across folders, and flattening would silently overwrite one with the other.
Source: shared/src/MantlePlace.Client/LocalBundleArchive.cs

**A retained file that already matches the published digest is reused, not rewritten.** The placing host, such as AutoCAD with an attached image, can hold the file open, and a second import would throw.
Source: shared/src/MantlePlace.Client/LocalBundleArchive.cs

**The manifest member's sha256 is the root of trust for per-file digests.** A view with a tampered manifest would carry tampered digests that every file then matches.
Source: shared/src/MantlePlace.Client/LocalBundleArchive.cs

### Vault client

**A level download is checked against the size the link states, not the listing's size and digest.** The listing's values describe the Max download of every file; inner files are then verified against manifest digests at import.
Source: shared/src/MantlePlace.Client/VaultClient.cs

**Materialize polling takes the requested tokens as an argument.** The platform answers with a delivery-state document that carries no status word, so completion can only be decided by comparing delivery against the tokens requested.
Source: shared/src/MantlePlace.Client/VaultClient.cs

**The poll's failure cap counts consecutive failures only, and running out of polls is reported separately from failure.** A job that survives scattered transient errors is not abandoned, and the two outcomes tell the curator opposite things.
Source: shared/src/MantlePlace.Client/VaultClient.cs

**A 401 triggers one renewal and retry, not a pre-flight expiry check, and renewal declines when one is already in flight.** The platform decides, clocks cannot see revoked tokens, and rotating platforms reject a reused refresh token.
Source: shared/src/MantlePlace.Client/VaultClient.cs

**The background vault listing runs only at start, at sign-in and on a floored interval, never while the vault browser is open, and fails silently; it never prepares or downloads.** Nobody asked for it.
Source: shared/src/MantlePlace.Client/VaultNewsChecker.cs

### Prepare and re-join

**A process the user cannot inspect is treated as not the owner of an interrupted Prepare.** The owner was a Revit this user ran. Reading it dead risks one order polled twice, answered as one job, while reading it live loses the re-join.
Source: shared/src/MantlePlace.Client/InterruptedPrepareStore.cs

**An unreadable or unwritable Prepare record silently does nothing and never guesses.** A claim against a record this process could not read might take a Prepare that another Revit is still watching.
Source: shared/src/MantlePlace.Client/InterruptedPrepareStore.cs

**Re-joining interrupted Prepares makes no request of its own; it rides the vault news checker's next successful listing, armed once per sign-in.** Unrequested listings stay bounded and no second build is started (HPS-55, HPS-24).
Source: shared/src/MantlePlace.Client/PrepareRejoiner.cs

**The PrepareWatcher owns Prepares so they outlive the vault window, and a second Prepare of the same order joins the existing watch.** The platform answers joined and queues no second job, but a second poll loop would spend the rate budget twice on one order. Events fire on thread-pool threads and subscribers hop to the UI thread.
Source: shared/src/MantlePlace.Client/PrepareWatcher.cs

**The ledger entry is written before the first request and off the caller's thread.** The record may wait on another Revit's lock, and an entry written after the network call would miss a crash during it.
Source: shared/src/MantlePlace.Client/PrepareWatcher.cs

**A Prepare that was interrupted always ends Interrupted, however the step handled the cancel.** Struck off the ledger like any other ending, it would be lost for good instead of re-joined.
Source: shared/src/MantlePlace.Client/PrepareWatcher.cs

**When the materialize start reports nothing to build, no job exists and the watcher does not poll.** Polling would wait out the whole budget and time out; this is also the only vault route to import an already-complete bundle.
Source: shared/src/MantlePlace.Client/PrepareWatcher.cs

**Progress is reported through a custom relay on the calling thread, not Progress<T>.** Progress<T> posts to the context that built it, and the watcher builds it on a thread-pool thread with no context.
Source: shared/src/MantlePlace.Client/PrepareWatcher.cs

### Connected host and realtime

**The connected host never waits on the network on the caller's thread.** Route calls and fetches run on the pool, the desk is touched under one lock from any thread, and the host marshals its own calls.
Source: shared/src/MantlePlace.Client/ConnectedHostService.cs

**Realtime events fire on the client's own thread-pool loop thread, and subscribers must marshal before touching host objects and hand work off.** Handlers run inside the loop; commands queue to one writer for the socket.
Source: shared/src/MantlePlace.Client/RealtimeClient.cs

**The heartbeat interval is 25 seconds, matching Supabase's clients.** Realtime drops a socket that stays quiet for longer.
Source: shared/src/MantlePlace.Client/RealtimeClient.cs

**Watching an order leaves and rejoins the channel with the order added, followed by a catch-up.** Realtime fixes a channel's subscriptions at join time.
Source: shared/src/MantlePlace.Client/RealtimeClient.cs

**A local override of either realtime URL or key means the platform route is not asked, and a half-set pair stays off.** Pairing a configured location with the production key would guess which key opens which socket.
Source: shared/src/MantlePlace.Client/RealtimeLocator.cs

### Other client decisions

**Embedded families are written to a .part file, verified, then renamed into a per-build folder, and an existing file is trusted only on a matching digest.** A crash-truncated copy is then never handed to Revit as an unloadable family.
Source: shared/src/MantlePlace.Client/FamilyFileStore.cs

**A GeoPackage with no table of the requested name but exactly one table is read as that table when it holds a sole layer.** The producer names tables in CamelCase rather than the layer's snake_case name.
Source: shared/src/MantlePlace.Client/GeoPackageFile.cs

**GeoPackages are read through the system winsqlite3.dll by P/Invoke, not Microsoft.Data.Sqlite.** The no-package rule forbids the package, and its native e_sqlite3.dll could be shadowed by another add-in's copy inside Revit's process.
Source: shared/src/MantlePlace.Client/WinSqlite.cs

**An unreadable or malformed config.json falls back to compiled defaults rather than throwing.** Load runs during Revit's add-in load, where an exception costs the ribbon button and tells the curator nothing.
Source: shared/src/MantlePlace.Client/MantlePlaceEndpoints.cs

**Reveal never hands a missing file to Explorer, and the /select argument is built as one quoted string with UseShellExecute false.** /select on a missing file silently opens Documents and reports success, and the folder fallback creates nothing, so a deleted order folder is not resurrected.
Source: shared/src/MantlePlace.Client/ShellLauncher.cs

**I/O lives in a third Client assembly separate from the host shim, targeting portable net8.0 with no NuGet packages.** Promote-by-rename and the secret store are then tested on hosted runners, and the null-store fallback is exercised on Linux.
Source: shared/src/MantlePlace.Client/MantlePlace.Client.csproj

**Generated enums use a custom EnumMember converter.** System.Text.Json's built-in converter writes C# names the platform refuses, and the JsonStringEnumMemberName fix needs .NET 9 while this assembly targets net8.0.
Source: shared/src/MantlePlace.PlatformApi/EnumMemberJsonConverter.cs

**PlatformTransport returns the status and raw body, and sends the caller's JSON in place of the generated empty body.** The host's own parsers and body rules cannot be expressed by the spec's generated types.
Source: shared/src/MantlePlace.PlatformApi/PlatformTransport.cs

### WPF brand chrome

**The primary button Style is built once per UI thread.** A WPF Style has thread affinity until sealed and Revit runs several UI threads, so one shared instance would be refused by other threads.
Source: shared/src/MantlePlace.Wpf/BrandChrome.cs

**Every plugin window sets the Mantle Place mark as its title-bar icon, with all renders as frames.** A WPF window with no icon shows the host's logo, which an app store review refuses.
Source: shared/src/MantlePlace.Wpf/BrandChrome.cs

**The title-bar icon is a multi-frame TIFF built per window on that window's thread.** TIFF is the only multi-frame format WPF encodes and decodes with alpha, and decoders are thread-bound.
Source: shared/src/MantlePlace.Wpf/BrandChrome.cs

**The primary button replaces its whole template instead of setting Background.** The hosts' Aero2 theme repaints the background with a theme brush on hover and press, which loses the brand orange.
Source: shared/src/MantlePlace.Wpf/BrandChrome.cs

**Mark renders are read as manifest resource streams, not pack URIs.** AutoCAD loads the plugin into its own load context, where WPF's by-name assembly lookup for pack URIs fails.
Source: shared/src/MantlePlace.Wpf/MarkImages.cs

### Import window stacking

**The foreground watch never asks the host's thread anything and avoids GetWindowText on host windows.** GetWindowText sends the host window a message and would make the import window wait on the host.
Source: shared/src/MantlePlace.Wpf/ForegroundWatch.cs

**The WinEvent callback delegate is held in a field for the hook's lifetime.** Windows calls through it, and a garbage-collected delegate crashes the process.
Source: shared/src/MantlePlace.Wpf/ForegroundWatch.cs

**The first show's z-order is set by rewriting WM_WINDOWPOSCHANGING, not by moving the window beforehand.** WPF's Show raises the window to the top and would undo an earlier move.
Source: shared/src/MantlePlace.Wpf/ForegroundWatch.cs

### Import window threading and behavior

**Every IImportWindowHost call, and everything else crossing between the import window thread and the host, is posted and never invoked; every fault on the window thread is caught.** The host API is single-threaded, the window's thread must never wait on the host's, and an exception leaving that thread ends the host process.
Source: shared/src/MantlePlace.Wpf/IImportWindowHost.cs, shared/src/MantlePlace.Wpf/ImportWindowThread.cs

**The import window lives on its own unowned thread and touches nothing of the import; closing it during a run requests a cancel.** The work runs on the host's thread, with no other place to stop it.
Source: shared/src/MantlePlace.Wpf/ImportWindow.cs

**The step clock repaints every 250 ms rather than every second.** A one-second timer that fires slightly early reads the same second twice, and the clock looks stuck.
Source: shared/src/MantlePlace.Wpf/ImportWindow.cs

**Layer checkboxes hook Checked and Unchecked instead of Click.** A UI Automation Toggle used by screen readers changes IsChecked without raising Click, which would leave the box and the imported layers out of step.
Source: shared/src/MantlePlace.Wpf/ImportWindow.cs

**The shared WPF project targets net8.0-windows, references no host API, and is not built in CI.** net8.0-windows is the floor every supported host runs, and the headless suites read its source instead.
Source: shared/src/MantlePlace.Wpf/MantlePlace.Wpf.csproj

### Other windows

**The notice popup never takes focus, using ShowActivated false and WS_EX_NOACTIVATE.** A curator typing into a view would otherwise lose their next keystroke, and neither host has a notification API.
Source: shared/src/MantlePlace.Wpf/NoticePopup.cs

**Window event handlers are wrapped in Guarded so no exception escapes.** A throw from a handler lands on the host's message pump, which has no add-in filter, and takes the whole application down.
Source: shared/src/MantlePlace.Wpf/SignInWindow.cs

**The vault browser is modeless, and closing it does not cancel a Prepare, which belongs to the session's watcher.** A materialize can take ten minutes, and the close box means done looking, not discard.
Source: shared/src/MantlePlace.Wpf/VaultBrowserWindow.cs

**A changed vault row is re-seated by assigning the list item again.** A ListBox of plain objects does not re-read ToString on its own, and re-seating also keeps the selection.
Source: shared/src/MantlePlace.Wpf/VaultBrowserWindow.cs
