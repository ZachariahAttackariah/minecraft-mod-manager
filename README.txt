Minecraft Mod Manager
=====================

Double-click MinecraftModManager.exe. It opens in its own window.
Close the window to quit. Opening the exe again while it's running just brings the window to the front.
The window remembers its size and position. Like Firefox, the tabs sit in the window's title bar:
drag the empty part of it to move the window, double-click it to maximize.

Sections (along the top): Mods, Libraries, Packs, Server, Snapshots, Cleanup, and Settings (the gear).

Settings (the gear at the right end of the section tabs)
  The defaults are the intended look, so nothing here needs changing. Saved in the app's settings file.
  Look .............. theme (dark, light, or follow Windows), accent colour (PLAY, switches that are on, the
                      current tab), row spacing (comfortable / compact), tag colours (vivid / soft), blocky
                      lettering for the title, PLAY and headings.
  Mod list .......... show file names, row buttons always or on hover, tags per row (3, 5 or all; the rest
                      fold into +N), ask before deleting.
  Sources ........... where Add mods searches (both sites, Modrinth or CurseForge), and the CurseForge API
                      key (tested before it's kept; Turn off removes it).
  Play .............. check mods before Play: runs the pre-launch check first and only stops you when
                      something would keep the game from starting ("Play anyway" is there too).
  Updates ........... where updates come from (see Updates).
  Detect sides ...... whether client mods that add features on the server are marked both sides.

Top right (like the Minecraft Launcher)
  Instance picker ... next to PLAY. Lists every instance with its Minecraft version, loader
                      and mod count, plus other mods folders you've used. Choosing an instance also
                      sets the Minecraft version and loader that Modrinth looks for.
                      "Shared settings..." is what instances can share (see below). "Other mods folder..."
                      picks any folder; "Open launcher" opens the launcher.
  New instance... ... at the bottom of the list. Name, Minecraft version, loader (NeoForge, Fabric, Forge,
                      Quilt), loader version, memory and what it shares, starting empty or as a copy of another instance
                      (mods, configs, resource and shader packs and settings; worlds only if you tick it).
                      Minecraft and the loader are downloaded the first time you press Play.
  ... on an instance  Open folder, Settings... (rename, Minecraft version, loader, loader version, memory,
                      what it shares), Duplicate..., Delete... (asks first, lists its worlds, moves the
                      instance to the Recycle Bin). Close the launcher before changing or deleting an instance:
                      while it's open it keeps its own copy of the settings and would undo the change (sharing
                      packs and game settings works either way). The instance being managed can't be deleted.
                      Switching an instance to another Minecraft version or loader leaves its mods folder as it
                      is; press Check mods afterwards to see which mods don't fit, and Modrinth can find matches.
  PLAY .............. starts the chosen instance.
  Pre-launch check .. under PLAY: Check mods (see below), the result, and "Show and fix".
  Server ............ at the right end of the section tabs: the server's status; click it for the Server page.
  Under the tabs: the mods folder (click it to open it; hover for the full path), its jar count, then
  "Open instance folder" (the folder the mods folder is in: configs, saves, packs) and Refresh.

Mods and Libraries
  Mods tagged library (or libraries) are kept out of the Mods list and shown in the Libraries tab next to
  it, with the same list and tools. Tag a mod library, or take the tag off, to move it between the two.
  Counts, the side chart and tag counts follow the tab you're on.

Mods page
  One bar above the list: Add mods on the left (its arrow also has "Add .jar or .zip files..." and the
  Modrinth and CurseForge websites), and on the right the Minecraft version and loader that updates and
  search look for, what's known about the jars, a check-now button, and "N updates", which shows what
  changed and updates them. Updates are checked by themselves every 30 minutes.
  Add mods searches Modrinth and CurseForge in one list (with a CurseForge key; Both / Modrinth /
  CurseForge at its top). A mod on both sites shows once, marked with both, and installs from Modrinth;
  if Modrinth has no file for your version and loader, CurseForge's is used instead.
  The list is in the middle, with "Where each jar installs" (client / server) on the left and the tags
  on the right; click either to filter the list. Hover over a tag to see what it means.
  Tag filters: click a tag card to show only mods with it, click again to hide mods with it instead
  (the card turns dashed and says NOT), and a third time to stop. Right-click a tag card, or Alt+click a
  tag on a row, to hide it straight away. Hidden tags beat shown ones.
  Detect (by "Where each jar installs") works out client only, server only or both for every jar: first
  from the jar itself (some say they're client or server only), then from Modrinth's client / server info.
  It lists what it found before anything changes: new side tags are ticked, best guesses and changes to
  sides you already set are not. Apply has Undo in the message.
  Each mod shows its icon (Modrinth's, else the logo inside the jar). On a mod's row, click a tag to filter
  by it (click again to stop). C and S show where it installs; change them from the right-click menu.
  Click the Mod or Side column heading to sort by name or by side (click again to reverse).
  The dots at the end of the filter row hold "Not in folder" and Copy filenames.
  "Not in folder" lists mods that still have tags in mod-tags.json but whose jar is gone (deleting keeps
  tags, in case the jar comes back). "Forget these" there, or Forget in the right-click menu, removes
  those leftover entries (with Undo); it never touches a jar that's in the folder.
  A turned-off mod shows OFF on its icon, and an available update is the green arrow among the row's
  actions, so neither makes the row taller. Every tag gets its own colour.
  Tags from another tool: if the folder has a .mod-tags.json keyed by mod id (another launcher's tag
  file), the list shows its tags, matched to jars by mod id, with tag names made valid ("To Do" shows as
  to-do). It updates as soon as that file changes. Nothing is written in such a folder (the other tool
  merges any mod-tags.json it finds into its own), so tags there are edited in that launcher only.

Tabs
  Links (Modrinth, CurseForge, mod pages) open as tabs in the same window, next to the pinned
  "Mod manager" tab. Each tab has back / forward / reload, an address bar and "Open in my browser".
  + ................. new tab (Modrinth)
  Middle-click / x .. close a tab
  Ctrl+T new tab, Ctrl+W close tab, Ctrl+Tab / Ctrl+Shift+Tab next / previous tab, Ctrl+1..9 jump to a tab,
  Ctrl+Shift+T reopen the last closed tab, Ctrl+L or F6 address bar.
  Any .jar you download in a tab goes straight into the current mods folder, and pack .zip files go to
  their pack folder (see Packs).

Packs
  Resource packs, shader packs and data packs, each with the same tools as mods: Search Modrinth (installs
  the right download for your Minecraft version), Add files... (.zip files, checked to be the right kind),
  Open folder, Check for updates / Update, and delete (to the Recycle Bin). Packs from Modrinth show their
  name, description and icon, and any update.
  Resource packs .... On / Turn on edits the game's list of resource packs (options.txt); the one turned on
                      last wins when two change the same thing. Close the game first.
  Shader packs ...... Use picks the shader the game starts with; "Turn shaders off" switches them off.
                      Each shader's own settings file goes with it when it's updated or deleted.
                      Shader packs need Iris (Oculus on Forge): adding a shader pack installs it, with
                      Sodium, which it needs, or turns it back on if it's turned off.
  Data packs ........ Choose where they go: the server's every-world folder (the default when there's a
                      server, since only the server's data packs count in multiplayer), the server's world,
                      this game's every-world folder, or one single-player world. Every-world folders work
                      through the Paxi mod: it's the datapacks folder next to mods (where CurseForge puts
                      data packs too), or config\paxi\datapacks if Paxi's own setting turns the first off.
                      Adding a data pack there installs Paxi (with YUNG's API) on that side, or turns it back
                      on. Restart the server after Paxi is added.
  Downloads ......... a pack .zip downloaded in a tab goes to the right place by itself: resource and shader
                      packs into this game, data packs into the default data pack folder. Other .zip files
                      go to Downloads.

Sharing between instances
  Shared settings... in the instance list shows what's shared. Each instance ticks what it shares in its
  own Settings. "New instances share everything" (on by default) applies to instances made here and to ones
  the app sees for the first time (made in the launcher, imported, or there before sharing existed): each is
  set up once, and anything you untick later stays unticked. A copy shares what its source does.
  When the shared Java arguments are still empty, the most recently played instance's own ones become them,
  so switching it over doesn't change how it runs.
  Java arguments .... one set for every instance that ticks it. An instance that doesn't keeps its own arguments
                      (edited in its Settings). Memory stays per instance, so -Xmx / -Xms aren't accepted.
                      These are the launcher's global Java arguments, so close the launcher before saving them.
  Shader packs,
  Resource packs .... one shared folder each. Ticking moves the instance's packs into it (a pack that's already
                      there is dropped, a different pack with the same name gets the instance's name added) and
                      links the instance's folder to it. Unticking gives the instance its own folder again, with
                      a copy of the shared packs. Deleting an instance never touches the shared packs.
  Game settings ..... video, sound, controls and key binds (options.txt). A change made in one game reaches the
                      others each time you press Play here (and when the app starts). Key binds for mods an
                      instance doesn't have are kept for the instances that have them. Which resource packs are
                      turned on only travels between instances that share resource packs.
  The shared files are in %LOCALAPPDATA%\MinecraftModManager\shared.

Play and the pre-launch check
  Play starts the game straight away. The pre-launch check only runs when you press Check mods (under
  the PLAY button). It looks for the things that stop NeoForge from starting: a required mod
  that's missing or turned off, the wrong version of a library, the same mod twice, mods for another
  Minecraft version, Fabric mods without Sinytra Connector. Anything it finds is listed with one-click
  fixes (turn it on, copy it from the server, search Modrinth).
  Duplicates: a mod that's in the folder more than once, on or off, is listed once with the copy to keep
  already picked: the one made for this Minecraft version, then the build for the instance's own loader
  (not a Fabric build run through Connector), then the version your other mods require, then the one
  that's on, then the newest version. "Remove the other copy" (or "Fix all duplicates") moves the rest to the Recycle Bin, moves
  their tags to the kept copy, and turns the kept copy on if the copy that was on is the one removed.
  Two or more extra copies at once are snapshotted first.

Mod details
  Click a mod's name (or right-click > Details...) for: its description, screenshots and links, every
  version for your Minecraft version and loader with its changelog (and "Switch to this" for any of them),
  what the mod needs and what needs it, and its config files (open them, and see whether the server's copy
  matches). It works the same for mods from Modrinth and from CurseForge (with a key); for a CurseForge mod
  whose author only allows downloads from their website, switching opens its page.
  Under the buttons: where its updates come from (see Updates), Pin this version, and a note.

Notes, pins and skipped updates (right-click a mod)
  Add a note... ..... a note on the mod: shown on hover over the note mark beside its file name, and found by
                      the search box. Also editable in its details.
  Pin this version .. Update all and the update arrow leave it alone (a pin mark shows beside its file name).
  Skip this update .. hides this one update; a newer version shows again. "Show the ... update again" undoes it.
  These are kept per mods folder in mod-extras.json in the app's data folder, and follow a mod through updates.

Drag and drop
  Drop .jar files anywhere on the window to add them to the mods folder, and resource, shader or data pack
  .zip files to send them where they belong (sorted by what's inside, like a download in a tab).

When the game crashes
  A red bar under the tabs says the game crashed (for crash reports from the last three days). "What went
  wrong?" reads the newest crash report, or latest.log when the game died without writing one, and lists
  the mods it points at, most likely first, with why: NeoForge's own "mod loading issue" lines, its
  suspected mods, the stack trace, and mixins that failed. For each: Turn it off (with Undo), Details, or
  "Find ..." when it's missing a mod it needs. Also in the dots menu and Ctrl+K ("Read the last crash").

What the app is doing
  While something runs (checking mods, looking for updates, updating, installing, detecting sides, adding
  files), a thin bar moves under the section tabs and its name shows by the server chip. Click it for
  what's running and the last few jobs, with how long they took and whether they worked.
  Messages stack at the bottom right; hover one to keep it, x to close it, and Undo where it applies.

Command palette and shortcuts
  Ctrl+K ............ type to jump to any mod (opens its details), tag (show only / hide), page or action.
  ? ................. every keyboard shortcut.

Turning off or deleting a mod others need
  If a mod that stays on requires it, the app says which ones first, and asks before going ahead.

Updates
  Every update first shows what changed: the changelogs of all versions between yours and the new one.
  Untick any mod you'd rather keep. Updating several mods takes a snapshot first.
  Release channel ... Settings > Updates: "Match" (the default) keeps a release on releases and lets a beta
                      move to newer betas or releases; or Releases only, Releases and betas, or Everything.
                      Each mod can override it in its details ("Updates from").

Server (for hosting friends on this PC)
  Servers ........... the dropdown at the top lists every server the app knows; pick one to switch (while
                      it's stopped). "Add existing folder..." adds a server you already have.
  Create server... .. makes a new server from one of your instances: name, location (by default
                      Minecraft Servers in your user folder), memory and port. It downloads the loader's
                      official server installer (NeoForge and Forge are checked against their published
                      checksums), runs it with the progress in the console, then copies the instance's
                      mods tagged "server" and its configs (not client-only ones). eula.txt is only written
                      if you tick "I accept Minecraft's EULA".
  Status ............ running / stopped, memory, uptime, who's online, and how friends connect.
  Start / Stop ...... Stop sends "stop", so
                      the world saves. Force stop only appears while it's stopping, for when it's stuck.
                      Nothing ever starts the server except the Start button.
  Console ........... everything the server prints, live. Filter to warnings & errors or players & chat,
                      type commands (Up/Down for history), quick buttons for list, save-all, whitelist, op, say.
  Sync from the game  compares the game's mods (and their client/server tags) with the server's:
                      mods to add, update, turn on or off, and the ones that are your call (client-only mods
                      on the server, server-only mods). Config files that differ are listed setting by
                      setting; choose "Game -> server" or "Server -> game" for each. Nothing changes until
                      Apply, and never while the server runs. Replaced jars go to the Recycle Bin; overwritten
                      configs are backed up in %LOCALAPPDATA%\MinecraftModManager\sync-backups.
  Settings .......... server.properties: name, max players, difficulty, whitelist, view distance and the
                      rest, plus every other line as text. Saved settings apply at the next start; the
                      previous file is kept as server.properties.bak.
  Closing the window while the server runs asks first, then stops the server properly and quits.

Snapshots
  A snapshot remembers every jar in the mods folder and whether it's on or off, and keeps a copy of each
  jar (in %LOCALAPPDATA%\MinecraftModManager\snapshots; a jar shared by several snapshots is stored once).
  Restore shows exactly what will change first, and saves how things are now as another snapshot.
  The app takes automatic snapshots before updating several mods, before restoring, and before finding a
  problem mod (the newest 10 automatic ones are kept; rename one to keep it).

Find a problem mod
  For a crash, lag or a bug when you don't know which mod causes it. Each round turns off half of the
  suspects (libraries stay on while a mod that's still on needs them); you play and answer "still there"
  or "gone". A bar at the top keeps track. At the end it puts everything back, optionally turning off the
  mod it found. Select mods in the Mods list first to search only among those.

Cleanup
  Old logs, crash reports, Java crash dumps, debug output and unfinished downloads in the game and server
  folders, Distant Horizons LOD data (marked "think first": it's rebuilt slowly), the built-in browser's
  cache and old sync backups. Shows sizes; everything goes to the Recycle Bin. Worlds, screenshots,
  configs and mods are never on the list.

Modrinth (built in, no account or key needed)
  Search Modrinth ... search panel for your Minecraft version and loader. Install adds the mod plus the mods
                      it needs, checks every download against Modrinth's checksum, and tags new jars
                      client/server from Modrinth's info. A second copy of a mod you already have is skipped.
  Status bar ........ how many jars Modrinth recognises and how many have updates (re-checked every 30 min).
  Minecraft x · y ... click it to change the Minecraft version and loader Modrinth looks for.

Managing mods
  Power button ...... disable / enable a mod (renames X.jar <-> X.jar.disabled, like the launcher does)
  Trash button ...... delete a mod (asks first, then moves the jar to the Recycle Bin)
  Checkbox / click .. select mods: Ctrl+click adds one, Shift+click selects a range, Ctrl+A selects all shown
  Selection bar ..... enable, disable, delete, update, tag or copy everything selected
  Right-click ....... actions on the left, tags on the right (tick to add, untick to remove, or type a new tag)

Manage tags (the Manage button above the tags, right of the list)
  Add, rename or delete tags, and describe what each means. client / server are the side tags the app relies
  on (sync uses them): their description can change, not their name.
  Careful: with many mods selected, clicking a half-filled tag in the right-click menu adds it to ALL of
  them, and clicking again removes it from all of them.

Tags live in mod-tags.json in the mods folder, matched by jar filename. Every tag change keeps the
previous version as mod-tags.json.bak. Every change to files is written to actions.log in this folder.

Good to know
  - Instances and Play run through Prism Launcher, which needs to be installed.
  - Close Minecraft before changing mods. Jars that are in use are refused.
  - Only this PC can reach the app's internal server, and only the app's own page can make changes.
  - It only touches .jar and .jar.disabled files directly inside the mods folders it manages, pack files in the
    pack folders, and the instance settings described above.
  - The built-in browser keeps its own sign-ins and cookies (including cookie-banner choices) in
    %LOCALAPPDATA%\MinecraftModManager\WebView2. Closing the app waits a moment for the browser to save them.

Files
  MinecraftModManager.exe                 The app (page embedded inside).
  Microsoft.Web.WebView2.*.dll,
  WebView2Loader.dll                      Microsoft's WebView2 parts for the app window. Keep them next to the exe.
  MinecraftModManager.settings.json       Mods folder, other folders used, server folder, Minecraft version, loader, window position.
  actions.log                             History of every change to mods, tags, the server and snapshots.
  source\Program.cs                       Core: the internal web server, Modrinth, tags.
  source\Window.cs                        The window, its tabs and the title bar they sit in.
  source\Instances.cs                     The instance picker, and creating / changing / deleting instances.
  source\Sharing.cs                       Sharing Java arguments, shader / resource packs and game settings.
  source\Packs.cs                         Resource, shader and data packs (and adding Iris / Paxi when needed).
  source\Server.cs                        Starting, stopping and talking to the Minecraft server.
  source\ServerCreate.cs                  Creating servers and switching between them.
  source\Sync.cs                          Comparing and syncing the game and the server.
  source\JarMeta.cs                       Reading jars and the pre-launch check.
  source\Details.cs                       Mod details, versions and changelogs.
  source\CurseForge.cs                    CurseForge: recognising jars, updates, search and install.
  source\CurseForgeDetails.cs             Mod details, versions and switching for CurseForge mods.
  source\ModExtras.cs                     Notes, pinned versions, skipped updates and release channels.
  source\Prefs.cs                         The Settings page.
  source\Drop.cs                          Files dropped on the window.
  source\Crash.cs                         Reading the last crash report.
  source\Sides.cs                         Detecting client / server / both.
  source\Duplicates.cs                    Finding duplicate jars.
  source\LinkedTags.cs                    Showing another tool's .mod-tags.json, read-only.
  source\Snapshots.cs                     Snapshots and finding a problem mod.
  source\Cleanup.cs                       Disk cleanup.
  source\mod-manager.template.html        The page.
  source\lib\                             The WebView2 DLLs used for building (from Microsoft's NuGet package).
  source\names.json                       Optional display names and Modrinth links for known jars (kept local).
  source\build.ps1                        Rebuilds the exe (and syntax-checks the page). Close the app first.
  source\make-icon.ps1, app.ico           The icon (the grass block from the instance picker).

Command line (optional): MinecraftModManager.exe --mods "D:\some\mods" --server "D:\server" --port 8737 --no-window
A mod-manager.html placed next to the exe replaces the built-in page, for quick edits without rebuilding.
