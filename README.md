# Minecraft Mod Manager

A Windows desktop app for managing a Minecraft mods folder and, optionally, a dedicated server that runs the same mods.

It's a small native window (WinForms + WebView2) with a local web page inside. The internal web server only listens on `localhost`, and only the app's own page can make changes.

## Features

- **Mods**
  - Turn mods on and off. Disabling renames `X.jar` to `X.jar.disabled`, the same as launchers do. Turning a mod on also turns on the turned-off libraries it requires (a setting, on by default).
  - Delete mods to the Recycle Bin.
  - Select several mods at once, file-manager style.
  - Tag mods (client / server, plus your own tags), stored in `mod-tags.json` next to the jars. Click a tag to show only mods with it; Ctrl+click to hide mods with it (a Setting can make a second click hide instead); the library tag only shows on the Libraries page, and new jars that Modrinth or CurseForge list as libraries get it automatically (a Setting); new jars without a client/server tag (installed, dropped, downloaded or copied in by hand) get the side Detect finds, and the libraries they require get their sides too (a Setting); sort the list by name or by side.
  - Detect sides: works out client only, server only or both for every jar: the jar's metadata (`clientSideOnly`, a Fabric `environment`), its code (`@Mod(dist = ...)`, read from the class files), Modrinth's client / server flags, then what the jar contains (block / item models and its own network packets mean both; CurseForge environment tags; client-only mixins mean probably client). Results are listed for review before any tag changes, with Undo after.
  - Each mod shows its icon: Modrinth's when Modrinth knows the jar, else the logo inside the jar. Turned-off mods show OFF on the icon; updates are an arrow among the row's actions.
  - Another tool's `.mod-tags.json` (keyed by mod id) in the same folder is shown live, matched to jars by mod id. It's read-only: nothing is written in that folder, since that tool merges any `mod-tags.json` it finds.
- **CurseForge** (with an API key in the local settings file, never in this repository)
  - Jars Modrinth doesn't know are recognised by CurseForge's file fingerprint: names, icons, update checks and updates.
  - Add mods searches Modrinth and CurseForge in one list. A mod on both sites (found by the same file hash on both, or the same slug with the same name or source repository) shows once and downloads from Modrinth (CurseForge's file is used only when Modrinth has none for the game version and loader). Mods install with their required dependencies. Clicking a result opens its preview (description, gallery, links, the version Install would get, and a Versions tab with changelogs where any listed version can be installed instead of the newest) in the same window as an installed mod's details, and "Installed" is checked against the current mod list.
  - Authors who turn off third-party downloads are respected: their mods are never downloaded by the app; their CurseForge page opens instead.
  - CurseForge data is kept in memory for the current run only.
- **Modrinth**
  - Recognises installed jars by their SHA-1 hash.
  - Checks for updates and shows every changelog between your version and the new one before updating.
  - Lets you switch to any older or newer version.
  - Searches and installs mods, together with the mods they require. Filters for category and side, and "more like this".
  - Checks every download against Modrinth's checksum.
- **Pre-launch check:** on demand (the Check mods button), the app reads every jar's `neoforge.mods.toml` / `mods.toml` / `fabric.mod.json`. It reports what would stop NeoForge from loading, with one-click fixes:
  - required mods that are missing or turned off, with Install it / Add all missing (found through the requiring mod's own Modrinth or CurseForge listing, then by id or name; each download checked to contain that mod). This includes what mods bundled inside a jar require: Create Aeronautics is a shell whose bundled parts need Create, Flywheel and Sable;
  - library versions outside the required range, and mods made for another Minecraft or NeoForge version, with Fix version / Fix all versions (candidate versions are opened and checked against every mod's requirements before anything switches);
  - the same mod twice, on or off. The copy to keep is picked automatically (made for this Minecraft version, then built for the instance's loader, then the version other mods require, then turned on, then newest), and one click moves the rest to the Recycle Bin and carries their tags over;
  - mods made for another Minecraft version;
  - Fabric mods without Sinytra Connector.
- **Mod details**
  - Description, links and versions with changelogs, for Modrinth and CurseForge mods alike; switch to any version.
  - A jar neither site publishes byte for byte (another build of the same mod) is matched by its mod id and name, and only offered updates when its own version is in the site's list.
  - Gallery: the mod's screenshots, full size on click (arrow keys to flip through).
  - Where its updates come from (match what's installed, releases, betas, everything), pin this version, and a note.
- **Notes, pins and skipped updates:** a note on any mod (shown in a card when the cursor rests on its row, found by search), pinning a mod so Update all leaves it alone, and skipping one update until a newer one comes out. Kept per mods folder in the app's data folder.
- **Features:** the structures, mobs, ores and biomes in the world gen, the mods' and Minecraft's own, read from their data (vanilla's from the game's jar in Prism's libraries), in tabs with a Mods / Minecraft filter, plus the items and recipes.
  - Ores: placed features whose configured feature is an ore, whether a mod adds them to biomes (`add_features`) or a biome lists them itself; with their blocks, height range, veins per chunk and vein size. Turning one off adds a `remove_features` rule for every biome to the toggle jar.
  - Biome switches: a biome is switched off through its mod's own config setting for it (a true/false setting named after it, or `"id": true` in a JSON list), changed in the game's config and the server's copy, with a backup. Minecraft's biomes, and mods without such a setting, have no switch.
  - Vanilla structures, mobs and ores turn off the same way as modded ones: a no-biomes copy of the structure, `remove_spawns` and `remove_features`.
  - Mobs lists every one of Minecraft's (spawn eggs plus the few without), including those only structures spawn (blazes in fortresses, from `spawn_overrides`; turning one off writes a copy of the structure without it). Mobs that only the game's code spawns (villagers, raids, the wither) say how they turn up and have no switch.
  - Biomes: the mods' and Minecraft's, with how many of the mods' structures and mobs turn up in each; a number opens Structures or Mobs filtered to that biome. Beside Structures and Mobs, a biome list (with counts) filters by biome, and a biome chip in a row does the same. Shared groups are opened up using the game's and NeoForge's own biome tags from Prism's libraries, so a structure listed under "Forest biomes" shows up for Grove.
  - Where a mod's config sets a feature (spawn weights, biome switches, structure settings), the row has a Config link that opens the Configs page at that setting; failing an exact setting, it opens the mod's config file with settings of that kind.
  - Each row shows the mod that adds it, the dimension and the biomes (a mod's own biome tags are opened up to their biomes; shared tags such as `#c:is_forest` show as a group), and how common it is: a structure's spacing from its structure set, a mob's spawn weight and group size.
  - Search, filter by on / off, dimension and mod (each with counts), sort by name, mod, dimension or how common, and select several rows to turn on or off at once, as in the Mods list.
  - The switch turns a structure or mob off. The app writes `mod-manager-feature-toggles.jar`, a small data-only mod in the mods folder that loads after the mods it overrides: a turned-off structure gets a copy with no biomes, so it stops generating in new chunks; a turned-off mob gets a `remove_spawns` rule for every biome. A mod's extra spawns of a vanilla mob have their own row, and turning that off replaces just that mod's rule with `none`. The list of what's off is kept inside the jar, so copying it to the server carries it over; with nothing off, the jar is removed. NeoForge and Forge only.
  - Items: every item and block from the mods' item models and language files (and the game's own), with its icon from its own texture, whether it's a block, and how many recipes make and use it (a count opens Recipes filtered to them). The switch shows or hides it in JEI's item list, written to JEI's own `config/jei/blacklist.json` (the file its edit mode keeps), so the item stays in the game.
  - Recipes: every recipe the game loads, with its result, kind (crafting, smelting, a mod's machine), ingredients and the mod that changed it, if another mod overrides it. Compat recipes whose `mod_loaded` conditions fail are left out. Turning one off puts a never-loading copy (a `false` condition) in the toggle jar, so the game skips it, and JEI with it.
- **Key binds:** every control in `options.txt`, Minecraft's and the mods', named from the jars' language files. A keyboard and mouse map shows which keys are free, taken or clashing, and a click on a key lists its binds. Filter by clashes, unbound or changed; click a bind's key and press a new key or mouse button (with Shift, Ctrl or Alt for NeoForge's modifiers) to change it; unbind or reset Minecraft's own to their defaults, several at once, with Undo. `options.txt` is backed up first, and the change is shared with other instances that share game settings.
- **Turning off or deleting a library** that other mods need says which ones first.
- **World backups:** zip any world (game or server) with restore; optionally the latest world before updates and before removing worldgen mods.
- **What changed:** on opening, new updates and jars added or gone since the folder was last open (optional).
- **Drag and drop:** `.jar` files dropped on the window go into the mods folder; pack `.zip` files are sorted by what's inside them.
- **Command palette (Ctrl+K)** to jump to any mod, tag, page or action; **?** lists every shortcut.
- **Auto-tag:** suggests your existing tags from each mod's Modrinth / CurseForge categories, reviewed per tag before anything changes, with Undo.
- **Modpacks:** export as `.mrpack` or CurseForge `.zip` (mods listed by where they're published, optional overrides and configs); import either into a new Prism instance with hash-checked downloads.
- **Compare two folders:** what each has, version, on/off and tag differences, matched by mod id. Read-only.
- **List conveniences:** arrow keys, Enter, Space and Delete in the list; double-click for details; search shortcuts (`#tag`, `-#tag`, `is:off`, `is:update`, `is:new`, `is:unneeded`, `side:client`…); sort by newest or largest; filters remembered per folder; "new" marks; libraries nobody requires marked "not needed"; Show in folder; copy the mod list with links; a recent-changes history from `actions.log`.
- **On/off presets:** named sets of which mods are on, switched in one click (with a preview, a snapshot first, and Undo).
- **Crash reader:** after a crash, a banner offers to read the newest crash report (when it finds a mod the crashed mods need that isn't loaded, a box at the top offers "Turn on ..." for a turned-off jar that has it, "Install ..." otherwise, or "Fix all") (or `latest.log`, or the earlier mod-loading report when the newest crash is only its aftermath in the same launch) and lists the mods it points at, most likely first, with how sure it is and the root cause (the last "Caused by"). Real mixin failures and missing classes count most: a missing class is traced to the jar that has it (a turned-off one gets "Turn it on") or to the missing mod the failing one requires ("Find …"). Stack lines are traced to jars by package; mods that only patched running code barely count.
- **Jobs:** a thin progress bar under the tabs while anything runs, and a list of running and recent jobs; updates, pack imports, world backups and multi-file adds can be stopped between steps. Messages stack with Undo where it applies.
- **Check before Play** (optional): the pre-launch check runs first and only interrupts when something would stop the game.
- **Deep check:** the pre-launch check also reads the mods' class files: a mod whose startup code or mixins use a library no installed mod has (one it doesn't list) is caught (startup code is followed call by call from the mod's main class, since Java only checks a class when it's first used: support for optional mods that only runs later, or behind an "is it installed?" check, doesn't count), as is a mod made for a different version of a mod it depends on (a class or method it calls isn't there), with "Pick another version". Optional-mod code (including code in a class that first checks whether a mod that isn't on is loaded), mixins whose target is absent and the game's own libraries are left alone; mods bundled inside bundled jars count, down to three levels; on a 250-mod pack it finds exactly the real problems. First run a few seconds, then cached.
  - What a mod needs and what needs it, including mods bundled inside jars, as a list or a full dependency tree.
  - Its config files, and whether the server's copies match.
- **Server**
  - A compact header: status, uptime, players, memory, time to the next scheduled restart, an "out of sync" badge, and Start / Restart / Stop.
  - Starts and stops a NeoForge dedicated server the way its `run.bat` would. Start warns first when the server's mods don't match the game.
  - Tabs: Overview (how friends join, details), Console (filters, command input and history), Mods & configs, Players, Settings and Backups.
  - **Players:** who's online (op, kick, ban), the whitelist, operators and bans. Changes go in as commands while the server runs, or straight into its JSON files while it's stopped (UUIDs from its user cache or Mojang).
  - **Settings:** `server.properties` in sections (joining & access, gameplay, performance), the server's memory (`user_jvm_args.txt`, keeping a fixed `-Xms` in step), plus scheduled restarts (6 / 12 / 24 h, with warnings in chat) and world backups.
  - **Backups:** the server world's backups with Back up now (saving paused while it runs) and Restore.
  - Asks before closing the window while the server runs, then stops it so the world saves.
- **Create server:** makes a new NeoForge, Forge or Fabric server from an instance. It runs the loader's official installer (NeoForge and Forge installers are checked against their published SHA-1), then copies the instance's server-side mods and configs and sets memory, port and name. The EULA is only accepted if the user ticks it. A dropdown switches between known servers.
- **Mods & configs (sync from the game):** compares the game's mods (using their client/server tags) with the server's, and config files setting by setting. A summary of counts by kind filters the list; each group can be ticked as a whole, and rows open for versions, files and notes. A game with lots of mods turned off (mid-test) isn't mirrored without a warning. **Sync with game** (on the server bar and in Mods & configs) does it all in one go with the game as the source: every default-ticked mod change plus every differing config copied to the server, after showing what will change; server-only mods/configs and kept mods stay, and a running server is stopped and restarted around it. **Server mods:** every jar in the server's mods folder, listed like an instance's mod list (icon, version, filter for all / kept / off, select-all and bulk actions), each with a pin to keep it on the server plus an on/off switch and delete. A kept mod is never turned off there by the sync, even when the game doesn't have it; one that's off on the server is offered "Turn on", newer versions from the game still go across, and the list has its own Add mods (search or .jar files) that installs straight onto the server and keeps them. Nothing changes until you apply; while the server runs, "Stop, apply and start" does it in one go. Configs changed in the game also follow to the server automatically (before it starts from here, and every few minutes while it's stopped); client-only configs, server-only files and server-only edits are left alone.
- **Snapshots**
  - Remembers every jar in the folder and whether it's on or off.
  - Keeps one copy of each jar, so a restore can bring back deleted or updated mods.
  - Taken automatically before bulk changes.
- **Find a problem mod:** a guided search that turns off half the suspects each round. Libraries stay on while something that's still on needs them, and everything is put back at the end.
- **Disk cleanup:** old logs, crash reports, Java crash dumps, debug output and Distant Horizons LOD data, all moved to the Recycle Bin.
- **Tabs:** links open as tabs in the same window, and the tab strip doubles as the title bar (Firefox-style, with the window's own snap, resize and maximize behaviour). The manager is a small icon-only tab, followed by Modrinth and CurseForge tabs that are always there (loaded when first opened; links to those sites open in them). A `.jar` downloaded in any tab goes straight into the mods folder, and a pack `.zip` into its pack folder.
- **Instances and Play:** launcher-style, at the top right: an instance picker next to a big PLAY button, with the pre-launch check under them. The server's status sits at the end of the section tabs, and the Mods page shows its client/server and topic-tag filters on either side of the list.
  - The picker lists every [Prism Launcher](https://prismlauncher.org/) instance with its Minecraft version, loader and mod count, plus other mods folders you've used.
  - Choosing an instance also sets the Minecraft version and loader used for Modrinth lookups.
  - PLAY launches the chosen instance through Prism, which stays out of sight (a Setting): it starts minimised, the instance's console is set not to open, and its window only comes back if it needs you (signing in, say). The app follows the game instead: PLAY shows Starting… / Playing with **Game log** (the live `latest.log`, filterable by warnings and errors) and **Stop** beside it; a crash opens the crash reader by itself, and if Prism stops before the game starts, its reason is shown. Games started from Prism itself are picked up too.
  - Instances can be created (empty, or as a copy of another), duplicated, renamed, given more or less memory, switched to another Minecraft version, loader (NeoForge, Fabric, Forge, Quilt) or loader version, and deleted (to the Recycle Bin). They are written in Prism's own format, and Prism downloads the game files on first launch.
- **Packs:** resource packs, shader packs and data packs get the same tools as mods: search on Modrinth and CurseForge (one merged list, like mods), install, hash-based identification and updates, adding `.zip` files (checked by what's inside), and deleting to the Recycle Bin. Each list has selection boxes like the mod list (a select-all box, Shift+click for a range) for updating, turning resource packs on or off, and deleting several at once.
- **Configs:** a page with every mod's config files (the game's and the server's), grouped by mod and marked where the server's copy differs or only the server has one. TOML configs open as settings pages, built from what NeoForge writes above each setting: switches, number boxes with their range, dropdowns for allowed values, one-per-line lists, each with its description, its default and a Reset when it's been changed. JSON configs show their values the same way; anything else (or "Edit as text") opens in a text editor. The search box also finds settings in every config at once. Saving rewrites only the changed lines, keeps a backup under `sync-backups`, refuses a file that changed on disk since it was opened, and waits for the game (or the server, for its copies) to be closed. A mod's details show its settings straight away on the Config files tab, in the same editor (a list at the top picks the file when it has several).
  - Resource packs are turned on and off in `options.txt`; the shader in use is set in Iris's config.
  - Data packs go into a world, the server's world, or every world through Paxi (the `datapacks` folder next to `mods`, which Paxi loads by default, or `config/paxi/datapacks` when Paxi's config turns that off), on the game or the server.
  - Adding a shader pack installs Iris (Oculus on Forge) and its dependencies if they're missing; adding a data pack to a Paxi folder does the same for Paxi on that side. A mod that's there but turned off is turned back on instead.
  - A pack `.zip` downloaded in a tab is sorted into the right folder by what's inside it.
- **Sharing between instances:** each instance opts in or out of each part separately, and instances share everything by default (including ones made in the launcher, the first time the app sees them).
  - Java arguments: one shared set (Prism's global JVM arguments); an instance that opts out keeps its own.
  - Shader packs and resource packs: one shared folder each, linked into every sharing instance with a directory junction. Opting in merges the instance's packs into the shared folder without losing any; opting out gives it its own copy.
  - Game settings (`options.txt`): merged into a shared copy and handed back out on every Play. Keys an instance doesn't know (another version's options, key binds of mods it doesn't have) are kept for the instances that use them.
  - Mod configs (opt-in per instance, not part of "share everything"): the `config` folders are kept in a shared copy. A config whose settings changed in one game (the newest change wins; comment-only rewrites don't count) is handed to every other sharing instance that has that mod, on Play and when the app starts. Joining takes the shared configs and adds the ones only that instance has. Overwritten files are backed up.

- **Settings** (the gear): quick looks (Overworld, Nether, The End, Deep Dark, Snowy), theme (dark, light or follow Windows), accent colour (or any colour), text size, and tags (vivid, soft, solid or outline) on top; under Fine-tune: background (glow, plain, block grid), a background picture of your own (dimmed, four strengths), square or rounded corners, row spacing, reduced motion, blocky lettering and `user.css`. The mod list can show or hide the Version, Side and Tags columns, and the app can open on a chosen page or wherever you left off. Also: file names, mod icons, row stripes, row buttons, tags per row, delete confirmation, search sources, the CurseForge key, and Detect's server-extras rule. The defaults are the intended look; only changes are saved, in the local settings file.
- **Your own tag colours**: click a tag's colour dot in Manage tags to pick any colour; "automatic colour" undoes it.
- **user.css**: a stylesheet in the app data folder, loaded after the app's own. Settings has Edit and Reload buttons; the first Edit makes the file with a few commented examples.

## CurseForge

With a CurseForge API key (pasted in Settings, kept in the local settings file, never in this repository):

- jars that are already in the mods folder are recognised by fingerprint;
- they are checked for updates, with changelogs;
- a file is downloaded only when the author allows distribution through the API, using the download link the API provides. Otherwise the app opens the mod's CurseForge page instead.

The app doesn't save or cache API data. Without a key, the CurseForge website can still be opened in a tab, and jars downloaded there go straight into the mods folder.

## Requirements

- Windows 10 or 11, which already includes .NET Framework 4.8 and the WebView2 runtime.
- Optional: Prism Launcher for Play, and Java 21 to run a NeoForge server.

## Building

```
powershell -ExecutionPolicy Bypass -File source\build.ps1
```

The script uses the C# compiler that ships with Windows (no SDK needed). It syntax-checks the page, embeds it into `MinecraftModManager.exe` in the folder above `source`, and copies Microsoft's WebView2 DLLs next to it. The DLLs are included in `source\lib` under their own license (`WebView2-LICENSE.txt`).

`README.txt` is the user guide.

## Layout

| Path | What it is |
| --- | --- |
| `source/Program.cs` | Core: the local web server, Modrinth, tags |
| `source/Window.cs` | The window, its tabs, and the custom title bar they sit in |
| `source/AdBlock.cs` | Blocks ads in the tabs with [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home) (GPLv3, by Raymond Hill), fetched from its GitHub releases into the data folder; not part of this repository or the exe |
| `source/Instances.cs` | The instance picker, and creating, changing and deleting Prism instances |
| `source/Sharing.cs` | Shared Java arguments, shader / resource packs and game settings between instances |
| `source/Packs.cs` | Resource, shader and data packs, and installing Iris / Paxi when a pack needs them |
| `source/CurseForgePacks.cs` | Searching and installing resource, shader and data packs from CurseForge |
| `source/Server.cs` | Starting, stopping and talking to the Minecraft server |
| `source/ServerCreate.cs` | Creating servers, and switching between them |
| `source/Sync.cs` | Comparing and syncing the game and the server |
| `source/JarMeta.cs` | Reading jar metadata, and the pre-launch check |
| `source/Details.cs` | Mod details, versions and changelogs |
| `source/CurseForge.cs` | CurseForge: fingerprints, updates, search and install |
| `source/Prefs.cs` | The Settings page's preferences |
| `source/Appearance.cs` | The background picture and user.css |
| `source/CurseForgeDetails.cs` | Mod details, versions and switching for CurseForge mods |
| `source/Drop.cs` | Files dropped on the window |
| `source/Presets.cs` | On/off presets |
| `source/Compare.cs` | Comparing two mods folders |
| `source/AutoTag.cs` | Site categories for Auto-tag |
| `source/DepTree.cs` | The dependency tree in mod details |
| `source/Worlds.cs` | World backups |
| `source/ServerSchedule.cs` | Scheduled server restarts and backups |
| `source/Modpack.cs` | Modpack export (.mrpack / CurseForge zip) and import |
| `source/FixVersions.cs` | Finding versions that fit for the pre-launch check |
| `source/MissingDeps.cs` | Finding and installing mods the pre-launch check finds missing |
| `source/CrossSite.cs` | Finding the Modrinth twin of each CurseForge search result |
| `source/Loose.cs` | Matching jars by mod id and name when no file matches exactly |
| `source/Crash.cs` | Reading the last crash report and naming the mods it points at |
| `source/ModExtras.cs` | Notes, pinned versions, skipped updates and release channels |
| `source/Sides.cs` | Detecting which side (client, server, both) each jar belongs on |
| `source/SideClues.cs` | What a jar's classes, mixins and assets say about its side |
| `source/Duplicates.cs` | Finding duplicate jars and picking the copy to keep |
| `source/LinkedTags.cs` | Showing another tool's `.mod-tags.json`, read-only |
| `source/Snapshots.cs` | Snapshots and finding a problem mod |
| `source/Cleanup.cs` | Disk cleanup |
| `source/Features.cs` | The Features tab: structures and mobs from the mods' data, and the toggle jar that turns them off |
| `source/Items.cs` | The Features tab's Items and Recipes: reading them from the jars, item icons, JEI's hidden list, and turned-off recipes in the toggle jar |
| `source/KeyBinds.cs` | The Key binds page: the controls in `options.txt`, their names and mods, and changing them |
| `source/mod-manager.template.html` | The page shown in the window |
| `source/build.ps1` | Build script |
| `source/make-icon.ps1`, `source/icon-source.png` | Builds the icon (`app.ico`) from the source picture: Minecraft's chiseled bookshelf, which belongs to Mojang (see Credits and `icon-source-NOTICE.txt`) |
| `source/fonts/` | The pixel font for the title, PLAY and headings: [Minecraft](https://github.com/IdreesInc/Minecraft-Font) by Idrees Hassan, SIL Open Font License 1.1 (`Minecraft-OFL.txt`). Built into the page, so it works offline |

## Credits

- **App icon:** Minecraft's chiseled bookshelf block. It isn't this project's work: the block, its textures and the Minecraft name belong to Mojang AB and Microsoft. It's used as Mojang's Minecraft Usage Guidelines allow for free, non-commercial fan projects (see `source/icon-source-NOTICE.txt`).
- **Pixel font:** [Minecraft](https://github.com/IdreesInc/Minecraft-Font) by Idrees Hassan, SIL Open Font License 1.1.
- **Ad blocking in the tabs:** [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home) by Raymond Hill, GPLv3, downloaded from its own releases, not included here.
- **Browser engine:** Microsoft Edge WebView2, under its own license (`source/lib/WebView2-LICENSE.txt`).

NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT. Minecraft is a trademark of Mojang AB. Mod information comes from Modrinth and CurseForge, which aren't affiliated with this project either.
