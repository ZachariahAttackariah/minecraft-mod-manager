Minecraft Mod Manager
=====================

Double-click MinecraftModManager.exe. It opens in its own window.
Close the window to quit. Opening the exe again while it's running just brings the window to the front.
The window remembers its size and position. Like Firefox, the tabs sit in the window's title bar:
drag the empty part of it to move the window, double-click it to maximize.

Sections (along the top): Mods, Server, Snapshots, Cleanup.

Bottom bar (like the Minecraft Launcher)
  Instance picker ... bottom left. Lists every instance with its Minecraft version, loader
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
  PLAY .............. bottom middle. Starts the chosen instance.
  Server ............ bottom right. The server's status; click it for the Server page.

Tabs
  Links (Modrinth, CurseForge, mod pages) open as tabs in the same window, next to the pinned
  "Mod manager" tab. Each tab has back / forward / reload, an address bar and "Open in my browser".
  + ................. new tab (Modrinth)
  Middle-click / x .. close a tab
  Ctrl+T new tab, Ctrl+W close tab, Ctrl+Tab / Ctrl+Shift+Tab next / previous tab, Ctrl+1..9 jump to a tab,
  Ctrl+Shift+T reopen the last closed tab, Ctrl+L or F6 address bar.
  Any .jar you download in a tab goes straight into the current mods folder.

Sharing between instances
  Shared settings... in the instance list shows what's shared. Each instance ticks what it shares in its
  own Settings; a new instance shares everything unless you untick it, and a copy shares what its source does.
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
  Play starts the game straight away. The pre-launch check only runs when you press Check mods (in the bar
  under the Modrinth status). It looks for the things that stop NeoForge from starting: a required mod
  that's missing or turned off, the wrong version of a library, the same mod twice, mods for another
  Minecraft version, Fabric mods without Sinytra Connector. Anything it finds is listed with one-click
  fixes (turn it on, copy it from the server, search Modrinth).

Mod details
  Click a mod's name (or right-click > Details...) for: Modrinth's description and links, every Modrinth
  version for your Minecraft version with its changelog (and "Switch to this" for any of them), what the
  mod needs and what needs it, and its config files (open them, and see whether the server's copy matches).

Updates
  Every update first shows what changed: the changelogs of all versions between yours and the new one.
  Untick any mod you'd rather keep. Updating several mods takes a snapshot first.

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

Manage tags (button on the Topic tags panel)
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
  - It only touches .jar and .jar.disabled files directly inside the mods folders it manages.
  - The built-in browser keeps its own sign-ins and cookies in %LOCALAPPDATA%\MinecraftModManager\WebView2.

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
  source\Server.cs                        Starting, stopping and talking to the Minecraft server.
  source\ServerCreate.cs                  Creating servers and switching between them.
  source\Sync.cs                          Comparing and syncing the game and the server.
  source\JarMeta.cs                       Reading jars and the pre-launch check.
  source\Details.cs                       Mod details, versions and changelogs.
  source\Snapshots.cs                     Snapshots and finding a problem mod.
  source\Cleanup.cs                       Disk cleanup.
  source\mod-manager.template.html        The page.
  source\lib\                             The WebView2 DLLs used for building (from Microsoft's NuGet package).
  source\names.json                       Optional display names and Modrinth links for known jars (kept local).
  source\build.ps1                        Rebuilds the exe (and syntax-checks the page). Close the app first.
  source\make-icon.ps1, lodestone.ico     The icon.

Command line (optional): MinecraftModManager.exe --mods "D:\some\mods" --server "D:\server" --port 8737 --no-window
A mod-manager.html placed next to the exe replaces the built-in page, for quick edits without rebuilding.
