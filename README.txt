Minecraft Mod Manager
=====================

Double-click MinecraftModManager.exe. It opens in its own window.
Close the window to quit. Opening the exe again while it's running just brings the window to the front.
The window remembers its size and position. Like Firefox, the tabs sit in the window's title bar:
drag the empty part of it to move the window, double-click it to maximize.

Sections (along the top): Mods, Libraries, Packs, Server, Snapshots, Cleanup, and Settings (the gear).

Settings (the gear at the right end of the section tabs)
  The defaults are the intended look, so nothing here needs changing. Saved in the app's settings file.
  Look .............. quick looks (Overworld, Nether, The End, Deep Dark, Snowy: theme, accent and
                      background in one click), theme (dark, light, or follow Windows), accent colour (five,
                      or any colour), text size (90 to 120%, the app window's zoom), and tags (vivid, soft,
                      solid or outline).
                      Fine-tune (click to unfold): background (glow, plain, or a faint block grid), a
                      background picture of your own (Choose a picture...; the app keeps its own copy,
                      dimmed so text stays readable: Bold, Clear, Soft or Faint), corners (rounded or
                      square), row spacing, animation (follow Windows, always, or reduce), blocky lettering for the
                      title, PLAY and headings, and your own styles: Edit user.css opens user.css in the
                      app data folder (made the first time with a few commented examples); save it, then
                      Reload it. Delete the file to undo it all.
                      Each tag's own colour: in Manage tags, click the tag's colour dot and pick any colour.
                      "automatic colour" next to it goes back to the usual one.
  Folders ........... the mods folder and the server folder (open or change), the app's data folder
                      (snapshots, backups, notes, presets) and the app's own folder (settings, actions.log).
  Mod list .......... open on (Mods, Libraries, Packs, Server, Snapshots, or wherever you left off),
                      turn on what a mod needs (on by default: turning a mod on also turns on the turned-off
                      libraries and mods it requires, the newest copy if there are several; Undo puts all back),
                      tag new mods' sides (on by default: a new jar with no client or server tag, however it
                      arrived, gets the side Detect finds, and libraries it requires get its sides too; tags
                      already there are never changed),
                      tag new libraries (on by default: a new jar Modrinth or CurseForge lists as a library, or
                      whose summary calls it a library/API/framework for mods, gets the library tag, so it's
                      listed under Libraries; installs, dropped jars and site-tab downloads count, updates keep
                      their tags),
                      the Version column (each mod's installed version; off by default), the Side column
                      (C / S) and the Tags column (tags still filter from the side panel when it's off),
                      mod icons on or off, rows with lines or stripes, tag order (A to Z, the default, or as in the file; client and server stay first,
                      and a tag keeps its colour either way), show file names, row buttons always or on
                      hover, tags per row (3, 5 or all; the rest
                      fold into +N), ask before deleting.
  Sources ........... where Add mods searches (both sites, Modrinth or CurseForge), and the CurseForge API
                      key (tested before it's kept; Turn off removes it).
  Play .............. check mods before Play: runs the pre-launch check first and only stops you when
                      something would keep the game from starting ("Play anyway" is there too).
  Updates ........... where updates come from (see Updates).
  Detect sides ...... whether client mods that add features on the server are marked both sides.
  Worlds ............ back up the latest world before risky changes (see World backups).

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
  search look for, what's known about the jars, Check for updates, and "N updates", which shows what
  changed and updates them. The app never looks for updates by itself: only Check for updates does (on
  opening, the jars are only recognised, so the line says "Updates not checked yet").
  Add mods searches Modrinth and CurseForge in one list (with a CurseForge key; Both / Modrinth /
  CurseForge at its top). A mod on both sites shows once, marked with both, and installs from Modrinth.
  Pairs are found by the same file on both sites (CurseForge's file hashes looked up on Modrinth), or the
  same slug with the same name or source repository; hover the Modrinth mark to see which.
  If Modrinth has no file for your version and loader, CurseForge's is used instead.
  Filters beside the search box: category (both sites), and side (Client only / Works on a server;
  Modrinth only, since CurseForge doesn't say). "More like this" on a result searches its category.
  The list is in the middle, with "Where each jar installs" (client / server) on the left and the tags
  on the right; click either to filter the list. Hover over a tag to see what it means. The arrow by
  each panel's title folds it away. With more than 20 tags, a filter box sits above them, and tags used
  as lists (to-do, testing, broken, check...) get their own group.
  Tag filters: click a tag to show only mods with it (click again to stop). Ctrl+click a tag (or right-click
  a tag card) to hide mods with it instead: the card turns dashed and says NOT; click it to stop. Hidden
  tags beat shown ones. Settings > Mod list > Hiding a tag can make a second click hide instead.
  The Libraries page's tag panel shows the tags its libraries have (library among them); the Mods page's
  shows every tag but library.
  Detect (by "Where each jar installs") works out client only, server only or both for every jar, trusting
  in this order: the jar's metadata (clientSideOnly, a Fabric environment); its code (an entry point that
  only loads on the client or the server); Modrinth's client / server info; then what's inside it (block
  or item models and its own network messages mean both; CurseForge's environment tags); and last, best
  guesses (registered content: probably both; only client-side patches: probably client only).
  It lists what it found before anything changes: new side tags are ticked, best guesses and changes to
  sides you already set are not. Apply has Undo in the message.
  Each mod shows its icon (Modrinth's, else the logo inside the jar). On a mod's row, click a tag to filter
  by it (click again to stop). C and S show where it installs; change them from the right-click menu.
  Click the Mod or Side column heading to sort by name or by side (click again to reverse).
  The dots at the end of the filter row hold "Not in folder", Copy filenames, and "Copy mod list with
  links" (the mods shown, as a list with Modrinth / CurseForge links, ready to paste into Discord).
  The sort menu beside the count sorts by name, side, newest first (when the jar was added or changed) or
  largest first. Filters, the search and the sort are remembered for each mods folder.
  Keys in the list: arrow keys move (Shift extends the selection), Enter opens a mod's details (so does a
  double-click), Space turns the selected mods off (or on), Delete deletes them.
  The search box also takes #tag and -#tag (with / without a tag), is:on, is:off, is:update, is:new,
  is:pinned, is:noted, is:unneeded and side:client / server / both / none, mixed with plain words.
  A jar added or changed in the last three days shows "new". On the Libraries page, a library no mod
  that's on requires shows "not needed" (it may still be used optionally, so check before removing it).
  Right-click > Show in folder opens Explorer with the jar selected.
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
  The title bar starts with the manager (the small tab with the app's icon), then a Modrinth and a
  CurseForge tab that are always there (each loads its site the first time you open it, and links to
  that site from the manager open in it), then any other tabs. Each tab has back / forward / reload,
  an address bar and "Open in my browser".
  Ctrl+click or middle-click on Modrinth / CurseForge opens another tab of that site.
  Middle-click / x .. close a tab
  Ctrl+T new Modrinth tab, Ctrl+W close tab, Ctrl+Tab / Ctrl+Shift+Tab next / previous tab, Ctrl+1..9 jump to a tab,
  Ctrl+Shift+T reopen the last closed tab, Ctrl+L or F6 address bar.
  Any .jar you download in a tab goes straight into the current mods folder, and pack .zip files go to
  their pack folder (see Packs).

Packs
  Resource packs, shader packs and data packs, each with the same tools as mods: Search (Modrinth, and
  CurseForge too with a key, as for mods: a pack on both sites shows once and installs from Modrinth, or
  from CurseForge when Modrinth has no fitting file; installs the right download for your Minecraft
  version), Add files... (.zip files, checked to be the right kind),
  Open folder, Check for updates / Update, and delete (to the Recycle Bin). Like the mod list, each pack
  has a box to select it (click the row, Shift+click for a range, or the box in the bar for all), and the
  bar acts on the selected ones: Update, Turn on / Turn off (resource packs) and Delete. Packs from Modrinth show their
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

Configs
  File list ......... every config file in the game's config folder and the server's, grouped by the mod it
                      belongs to. "server differs" and "server only" show where the two copies part ways.
  Search ............ the box finds files and, from two letters on, settings in every config at once;
                      clicking a setting opens its file at that setting.
  Settings page ..... TOML configs (what NeoForge mods write) show each setting with its description:
                      switches for true/false, number boxes with the allowed range, dropdowns where only
                      certain values are allowed, lists one per line. "changed from default" and Reset come
                      from the default the mod notes. JSON configs show their values the same way.
  Edit as text ...... the whole file in a text editor (the only view for other formats). JSON is checked
                      before it's saved.
  Game's / server's . when both have the file, switch between the copies. Your game's copy is the one that
                      reaches the server by itself (Server > Mods & configs).
  Saving ............ only the changed lines are rewritten (comments and layout stay); the old file is kept
                      under %LOCALAPPDATA%\MinecraftModManager\sync-backups\<time>\editor-game (or
                      editor-server). A file the game saved meanwhile isn't overwritten: open it again. Close
                      the game first (the server, for its files): they rewrite configs as they close.
  From a mod ........ its details > Config files > Edit opens that file here.

Sharing between instances
  Shared settings... in the instance list shows what's shared. Each instance ticks what it shares in its
  own Settings. "New instances share everything" (on by default) applies to instances made here and to ones
  the app sees for the first time (made in the launcher, imported, or there before sharing existed): each is
  set up once, and anything you untick later stays unticked. The New instance dialog starts with every
  box ticked (copies and modpack imports included); untick what an instance should keep to itself.
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
  Mod configs ....... opt-in per instance (its Settings; new instances don't share configs by themselves).
                      A config whose settings changed in one game (newest change wins; a mod rewriting the
                      same values or a comment doesn't count) reaches the other sharing instances each time
                      you press Play here (and when the app starts), but only instances that have that mod
                      or already have the file. Joining takes the shared configs and adds any only that
                      instance has. Overwritten files are backed up in ...\sync-backups\<time>\instances.
  The shared files are in %LOCALAPPDATA%\MinecraftModManager\shared.

Play and the pre-launch check
  The launcher stays out of sight (Settings > Play > "Keep the launcher out of sight", on by default):
  Prism still signs in and starts the game, but it starts minimised and the instance's console is set
  not to open. Its window comes back only if it's still busy after 25 seconds (a sign-in or a big
  download), or with "Show Prism". While the game starts and runs, PLAY says Starting... / Playing, and
  beside it "Game log" shows the game's log live (all, warnings and errors, or errors only, with a find
  box) and "Stop" ends the game (like closing it from Task Manager). When the game crashes, the crash
  reader opens by itself; when Prism stops before the game starts, the reason from its log is shown
  with "Open Prism". A game started from Prism itself is followed too.
  Play starts the game straight away. The pre-launch check only runs when you press Check mods (under
  the PLAY button). It looks for the things that stop NeoForge from starting: a required mod
  that's missing or turned off, the wrong version of a library, the same mod twice, mods for another
  Minecraft version, Fabric mods without Sinytra Connector. Anything it finds is listed with one-click
  fixes (turn it on, copy it from the server, install it, search for it).
  Missing mods: "Install it" on one, or "Add all missing" at the top for all of them at once (it turns on
  the ones that are off and copies the ones the server has too). A missing mod is only an id, often named
  differently on the sites ("fusion" is Fusion (Connected Textures)), so the app gathers likely mods: what
  the requiring mod lists as required on Modrinth or CurseForge, Modrinth projects whose address is the id
  (also split in two: farmersdelight -> farmers-delight), and both sites' searches. It downloads each to a
  temporary folder and opens it, and installs the first that really contains that mod id. Mods neither
  site has are named so you can find them yourself.
  Wrong versions: "Fix version" on one, or "Fix all versions". For a mod made for another Minecraft or
  NeoForge version, another version of it. For "X needs Y in a range", a newer X that accepts the Y here,
  then a Y in the range (its own jar when Y is only bundled inside another mod), and last an older X.
  Each candidate is downloaded and opened, and only picked when every mod's declared requirements still
  hold with it and the other planned switches in place. The plan is shown first; old jars go to the
  Recycle Bin, tags and on / off carry over. When nothing fits, it says so.
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
  A jar that neither site publishes byte for byte (a different build or a repack of the same mod) is
  matched by the mod id and name inside it, Modrinth first, CurseForge when Modrinth has no files for
  your version. Its details say "Matched by mod id and name". It's offered an update only when one of
  the site's files has the jar's own version number, so the app knows where it stands.
  Under the buttons: where its updates come from (see Updates), Pin this version, and a note.
  Needs & needed by has a Tree view: everything it requires, and what those require, with missing and
  turned-off ones marked; click a name to open that mod.

Notes, pins and skipped updates (right-click a mod)
  Add a note... ..... a note on the mod: shown on hover over the note mark beside its file name, and found by
                      the search box. Also editable in its details.
  Pin this version .. Update all and the update arrow leave it alone (a pin mark shows beside its file name).
  Skip this update .. hides this one update; a newer version shows again. "Show the ... update again" undoes it.
  These are kept per mods folder in mod-extras.json in the app's data folder, and follow a mod through updates.

Recent changes (Snapshots page)
  Everything the app changed, newest first, from actions.log, with a filter box.

World backups (Snapshots page)
  Every world of this game (and the server's world) with "Back up now", which zips it into the app's
  data folder (world-backups; the newest five per world are kept, older ones go to the Recycle Bin).
  Restore moves the current world to the Recycle Bin and puts the backup in its place; it waits until
  the world isn't open. Settings > Worlds can back up the latest world by itself before updates and
  before turning off or deleting worldgen mods; if that backup fails, the change stops.

What changed
  On opening, a note says what's new since the folder was last open here: new updates, and jars added
  or gone. Show lists them. Switch it off in Settings > Mod list.

Drag and drop
  Drop .jar files anywhere on the window to add them to the mods folder, and resource, shader or data pack
  .zip files to send them where they belong (sorted by what's inside, like a download in a tab).

Auto-tag (by the Tags panel's Manage button)
  Suggests your existing tags for mods from their Modrinth or CurseForge categories (optimization ->
  performance, technology, worldgen, food, magic, transportation -> transport, and so on), grouped by tag
  with a tick per mod. Only tags that already exist here are suggested, and only to mods without them;
  categories with no matching tag are listed so you can make one. Add tags has Undo.

Modpacks
  Export (dots menu or Ctrl+K) saves this instance as a Modrinth .mrpack or a CurseForge .zip in your
  Downloads folder. Mods that are on are listed by where they're published (Modrinth file links with
  their hashes; CurseForge project and file ids), so the pack stays small. Jars the site doesn't list,
  and the config folder, can go inside the pack (check those mods' licences before sharing publicly).
  Import (Add mods arrow, or Ctrl+K) takes a .mrpack or CurseForge .zip and makes a new instance with
  its Minecraft and loader versions, downloads every mod (checked against the pack's hashes; Modrinth
  packs only from the hosts the format allows), unpacks its files, and switches to it. CurseForge mods
  whose authors only allow website downloads are listed with an Open page button.

Compare with another folder (dots menu, or Ctrl+K)
  Pick an instance or a folder you've used: every mod is matched by mod id across both, and listed as only
  here, only there, another version, on in one and off in the other, or with different tags (the tags only
  one side has are outlined). Nothing is changed in either folder.

On/off presets (Presets, next to Add mods)
  A preset remembers which mods are on ("full", "performance", "testing"...). "Save what's on now" saves
  or updates one; Apply shows what it would turn on and off, takes a snapshot, switches, and offers Undo.
  Mods added after a preset was saved stay as they are. Presets find mods by file name, or by mod id after
  an update. Kept per mods folder in mod-presets.json in the app's data folder. Also in Ctrl+K.

When the game crashes
  A red bar under the tabs says the game crashed (for crash reports from the last three days). "What went
  wrong?" reads the newest crash report, or latest.log when the game died without writing one, and lists
  the mods it points at, most likely first, with why and how sure it is. What counts most: NeoForge's own
  "mod loading issue" lines and suspected mods, mixins that really failed ("Attach error ... from mod x"),
  and classes that couldn't be found, traced to the jar that has them (a turned-off one gets "Turn it
  on") or, when no jar has them, to the missing mod the failing one requires ("Find ..."). The root cause
  (the last "Caused by") counts double; stack lines are traced to jars by their Java package; mods that
  only patched code that was running count very little. For each: Turn it on / Turn it off (with Undo),
  Details, or "Find ...". Also in the dots menu and Ctrl+K ("Read the last crash").

What the app is doing
  While something runs (checking mods, looking for updates, updating, installing, detecting sides, adding
  files), a thin bar moves under the section tabs and its name shows by the server chip. Click it for
  what's running and the last few jobs, with how long they took and whether they worked. Updates, pack
  imports, world backups and adding several files have Stop: they stop after the current step (an update
  finishes the mod it's on; a backup in progress is thrown away). A message right after a long or failed
  job has "Job details".
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
  Header ............ running / stopped, uptime, players, memory, time to the next scheduled restart, and
                      "Out of sync" when the server's mods differ from the game's (click it for the list).
                      The ... button opens the server folder or logs, checks the server's mods, creates a
                      server or adds an existing folder.
  Start / Restart / Stop
                      Start warns first when the server's mods don't match the game (players whose mods
                      differ can't join). Restart stops it (the world saves) and starts it again. Stop sends
                      "stop"; Force stop only appears while it's stopping, for when it's stuck. The server
                      only starts from these buttons, "Stop, apply and start", or a scheduled restart.
  Overview .......... how friends join (with Copy buttons), the folder, world, mods, memory, restarts,
                      backups and Java.
  Console ........... everything the server prints, live. Filter to warnings & errors or players & chat,
                      type commands (Up/Down for history), quick buttons for list, save-all, say, day, weather.
  Mods & configs .... compares the game's mods (and their client/server tags) with the server's. The counts
                      at the top (Add, Update, Turn on, Turn off, Copy to game, Your call, No tag, Configs)
                      filter the list; each group ticks as a whole; a row opens for versions, files and the
                      reason. When the game has lots of mods turned off (testing?), turning them off on the
                      server isn't ticked for you. Config files that differ show setting by setting; choose
                      "Game -> server" or "Server -> game" for each. Nothing changes until you apply; while
                      the server runs the button is "Stop, apply and start". Replaced jars go to the Recycle
                      Bin; overwritten configs are backed up in %LOCALAPPDATA%\MinecraftModManager\sync-backups.
                      Configs follow your game automatically (on by default): a config whose settings you
                      changed in the game is copied to the server before it starts from here and every few
                      minutes while it's stopped. Client-only configs, configs only the server has and
                      changes made only on the server are left alone; the first look at a config that
                      differs only remembers it. Copies are backed up like the manual ones.
  Players ........... who's online (Op, Kick, Ban), the whitelist (and turning it on or off), operators and
                      bans. While the server runs, changes go in as commands; while it's stopped they're
                      written to whitelist.json, ops.json and banned-players.json (a new name's UUID comes
                      from the server's usercache.json, or from Mojang).
  Settings .......... server.properties in sections (joining & access, gameplay, performance), with every
                      other line as text; saved settings apply at the next start and the previous file is
                      kept as server.properties.bak. Memory (user_jvm_args.txt; a starting heap fixed to the
                      maximum moves with it) saves straight away. Restart on a schedule (off, 6, 12 or 24
                      hours; players are warned in chat 5 and 1 minutes ahead) and back up the world (at each
                      scheduled restart, or once a day while it runs, with saving paused); both are off by
                      default and only apply to a server started from this app.
  Backups ........... the server world's backups: Back up now (saving pauses for a few seconds while it
                      runs), Restore (with the server stopped) and Delete.
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
                      Click a result's name for its preview (in the same window as an installed mod's details):
                      description, gallery, links, what version Install would get, and Install itself. Its
                      Versions tab lists every version for your game version (newest first, with changelogs)
                      and "Install this" installs that one instead of the newest. Results
                      you already have say Installed, checked against the mod list as it is right now (matched
                      by Modrinth project, CurseForge project, or the mod's own id or name).
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
  source\CurseForgePacks.cs               Searching and installing packs from CurseForge.
  source\Server.cs                        Starting, stopping and talking to the Minecraft server.
  source\ServerCreate.cs                  Creating servers and switching between them.
  source\Sync.cs                          Comparing and syncing the game and the server.
  source\JarMeta.cs                       Reading jars and the pre-launch check.
  source\Details.cs                       Mod details, versions and changelogs.
  source\CurseForge.cs                    CurseForge: recognising jars, updates, search and install.
  source\CurseForgeDetails.cs             Mod details, versions and switching for CurseForge mods.
  source\ModExtras.cs                     Notes, pinned versions, skipped updates and release channels.
  source\Prefs.cs                         The Settings page.
  source\Appearance.cs                    The background picture and user.css.
  source\Drop.cs                          Files dropped on the window.
  source\Crash.cs                         Reading the last crash report.
  source\Presets.cs                       On/off presets.
  source\Compare.cs                       Comparing two folders.
  source\AutoTag.cs                       Categories for Auto-tag.
  source\DepTree.cs                       The dependency tree in mod details.
  source\Worlds.cs                        World backups.
  source\ServerSchedule.cs                Scheduled server restarts and backups.
  source\Modpack.cs                       Modpack export and import.
  source\Loose.cs                         Matching jars by mod id and name when no file matches exactly.
  source\Sides.cs                         Detecting client / server / both.
  source\Duplicates.cs                    Finding duplicate jars.
  source\LinkedTags.cs                    Showing another tool's .mod-tags.json, read-only.
  source\Snapshots.cs                     Snapshots and finding a problem mod.
  source\Cleanup.cs                       Disk cleanup.
  source\mod-manager.template.html        The page.
  source\lib\                             The WebView2 DLLs used for building (from Microsoft's NuGet package).
  source\names.json                       Optional display names and Modrinth links for known jars (kept local).
  source\build.ps1                        Rebuilds the exe (and syntax-checks the page). Close the app first.
  source\make-icon.ps1, app.ico           The icon, built from icon-source.png: Minecraft's chiseled bookshelf,
                                          which belongs to Mojang (see Credits and icon-source-NOTICE.txt).
  source\fonts\                           The pixel font for the title, PLAY and headings: "Minecraft" by
                                          Idrees Hassan (github.com/IdreesInc/Minecraft-Font), SIL Open Font
                                          License 1.1 (Minecraft-OFL.txt). Built into the app; works offline.

Command line (optional): MinecraftModManager.exe --mods "D:\some\mods" --server "D:\server" --port 8737 --no-window
  (--restart-minutes N makes scheduled server restarts and backups come every N minutes, for testing.)
A mod-manager.html placed next to the exe replaces the built-in page, for quick edits without rebuilding.

Credits
  App icon .......... Minecraft's chiseled bookshelf block. It isn't this project's work: the block, its
                      textures and the Minecraft name belong to Mojang AB and Microsoft. It's used as Mojang's
                      Minecraft Usage Guidelines allow for free, non-commercial fan projects
                      (source\icon-source-NOTICE.txt).
  Pixel font ........ "Minecraft" by Idrees Hassan (github.com/IdreesInc/Minecraft-Font), SIL Open Font
                      License 1.1.
  Ad blocking ....... uBlock Origin Lite by Raymond Hill (github.com/uBlockOrigin/uBOL-home), GPLv3, downloaded
                      from its own releases, not included here.
  Browser engine .... Microsoft Edge WebView2, under its own license (source\lib\WebView2-LICENSE.txt).

  NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT. Minecraft is a
  trademark of Mojang AB. Mod information comes from Modrinth and CurseForge, which aren't affiliated with
  this project either.
