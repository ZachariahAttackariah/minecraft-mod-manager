# Minecraft Mod Manager

A Windows desktop app for managing a Minecraft mods folder and, optionally, a dedicated server that runs the same mods.

It's a small native window (WinForms + WebView2) with a local web page inside. The internal web server only listens on `localhost`, and only the app's own page can make changes.

## Features

- **Mods**
  - Turn mods on and off. Disabling renames `X.jar` to `X.jar.disabled`, the same as launchers do.
  - Delete mods to the Recycle Bin.
  - Select several mods at once, file-manager style.
  - Tag mods (client / server, plus your own tags), stored in `mod-tags.json` next to the jars. Click a tag on a row to filter by it; sort the list by name or by side.
  - Detect sides: works out client only, server only or both for every jar, from the jar's own metadata (a client-only `displayTest`, `clientSideOnly`, or a Fabric `environment`) and then Modrinth's client / server flags. Results are listed for review before any tag changes, with Undo after.
  - Each mod shows its icon: Modrinth's when Modrinth knows the jar, else the logo inside the jar. Turned-off mods show OFF on the icon; updates are an arrow among the row's actions.
  - Another tool's `.mod-tags.json` (keyed by mod id) in the same folder is shown live, matched to jars by mod id. It's read-only: nothing is written in that folder, since that tool merges any `mod-tags.json` it finds.
- **CurseForge** (with an API key in the local settings file, never in this repository)
  - Jars Modrinth doesn't know are recognised by CurseForge's file fingerprint: names, icons, update checks and updates.
  - The Add mods dialog searches CurseForge too, and installs mods with their required dependencies.
  - Authors who turn off third-party downloads are respected: their mods are never downloaded by the app; their CurseForge page opens instead.
  - CurseForge data is kept in memory for the current run only.
- **Modrinth**
  - Recognises installed jars by their SHA-1 hash.
  - Checks for updates and shows every changelog between your version and the new one before updating.
  - Lets you switch to any older or newer version.
  - Searches and installs mods, together with the mods they require.
  - Checks every download against Modrinth's checksum.
- **Pre-launch check:** on demand (the Check mods button), the app reads every jar's `neoforge.mods.toml` / `mods.toml` / `fabric.mod.json`. It reports what would stop NeoForge from loading, with one-click fixes:
  - required mods that are missing or turned off;
  - library versions outside the required range;
  - the same mod twice, on or off. The copy to keep is picked automatically (made for this Minecraft version, then built for the instance's loader, then the version other mods require, then turned on, then newest), and one click moves the rest to the Recycle Bin and carries their tags over;
  - mods made for another Minecraft version;
  - Fabric mods without Sinytra Connector.
- **Mod details**
  - Description, links and versions with changelogs.
  - Gallery: the mod's Modrinth screenshots, full size on click (arrow keys to flip through).
  - What a mod needs and what needs it, including mods bundled inside jars.
  - Its config files, and whether the server's copies match.
- **Server**
  - Starts and stops a NeoForge dedicated server the way its `run.bat` would.
  - Live console with filters, command input and history.
  - Online players and connection info.
  - A `server.properties` editor.
  - Asks before closing the window while the server runs, then stops it so the world saves.
- **Create server:** makes a new NeoForge, Forge or Fabric server from an instance. It runs the loader's official installer (NeoForge and Forge installers are checked against their published SHA-1), then copies the instance's server-side mods and configs and sets memory, port and name. The EULA is only accepted if the user ticks it. A dropdown switches between known servers.
- **Sync from the game:** compares the game's mods (using their client/server tags) with the server's, and compares config files setting by setting. Nothing changes until you press Apply, and never while the server is running.
- **Snapshots**
  - Remembers every jar in the folder and whether it's on or off.
  - Keeps one copy of each jar, so a restore can bring back deleted or updated mods.
  - Taken automatically before bulk changes.
- **Find a problem mod:** a guided search that turns off half the suspects each round. Libraries stay on while something that's still on needs them, and everything is put back at the end.
- **Disk cleanup:** old logs, crash reports, Java crash dumps, debug output and Distant Horizons LOD data, all moved to the Recycle Bin.
- **Tabs:** links open as tabs in the same window, and the tab strip doubles as the title bar (Firefox-style, with the window's own snap, resize and maximize behaviour). A `.jar` downloaded in any tab goes straight into the mods folder, and a pack `.zip` into its pack folder.
- **Instances and Play:** launcher-style, at the top right: an instance picker next to a big PLAY button, with the pre-launch check under them. The server's status sits at the end of the section tabs, and the Mods page shows its client/server and topic-tag filters on either side of the list.
  - The picker lists every [Prism Launcher](https://prismlauncher.org/) instance with its Minecraft version, loader and mod count, plus other mods folders you've used.
  - Choosing an instance also sets the Minecraft version and loader used for Modrinth lookups.
  - PLAY launches the chosen instance through Prism.
  - Instances can be created (empty, or as a copy of another), duplicated, renamed, given more or less memory, switched to another Minecraft version, loader (NeoForge, Fabric, Forge, Quilt) or loader version, and deleted (to the Recycle Bin). They are written in Prism's own format, and Prism downloads the game files on first launch.
- **Packs:** resource packs, shader packs and data packs get the same tools as mods: Modrinth search, install, hash-based identification and updates, adding `.zip` files (checked by what's inside), and deleting to the Recycle Bin.
  - Resource packs are turned on and off in `options.txt`; the shader in use is set in Iris's config.
  - Data packs go into a world, the server's world, or every world through Paxi (the `datapacks` folder next to `mods`, which Paxi loads by default, or `config/paxi/datapacks` when Paxi's config turns that off), on the game or the server.
  - Adding a shader pack installs Iris (Oculus on Forge) and its dependencies if they're missing; adding a data pack to a Paxi folder does the same for Paxi on that side. A mod that's there but turned off is turned back on instead.
  - A pack `.zip` downloaded in a tab is sorted into the right folder by what's inside it.
- **Sharing between instances:** each instance opts in or out of each part separately, and instances share everything by default (including ones made in the launcher, the first time the app sees them).
  - Java arguments: one shared set (Prism's global JVM arguments); an instance that opts out keeps its own.
  - Shader packs and resource packs: one shared folder each, linked into every sharing instance with a directory junction. Opting in merges the instance's packs into the shared folder without losing any; opting out gives it its own copy.
  - Game settings (`options.txt`): merged into a shared copy and handed back out on every Play. Keys an instance doesn't know (another version's options, key binds of mods it doesn't have) are kept for the instances that use them.

## CurseForge

CurseForge API support is planned, pending an API key. The intended scope:

- recognise jars that are already in the mods folder (fingerprint lookup);
- check them for updates and show changelogs;
- download an update only when the author allows distribution through the API, using the download link the API provides. Otherwise the app opens the mod's CurseForge page instead.

The app won't save or cache API data, and the key will be kept in the local settings file, never in this repository. Until then, the CurseForge website can be opened in a tab, and jars downloaded there go straight into the mods folder.

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
| `source/Instances.cs` | The instance picker, and creating, changing and deleting Prism instances |
| `source/Sharing.cs` | Shared Java arguments, shader / resource packs and game settings between instances |
| `source/Packs.cs` | Resource, shader and data packs, and installing Iris / Paxi when a pack needs them |
| `source/Server.cs` | Starting, stopping and talking to the Minecraft server |
| `source/ServerCreate.cs` | Creating servers, and switching between them |
| `source/Sync.cs` | Comparing and syncing the game and the server |
| `source/JarMeta.cs` | Reading jar metadata, and the pre-launch check |
| `source/Details.cs` | Mod details, versions and changelogs |
| `source/Snapshots.cs` | Snapshots and finding a problem mod |
| `source/Cleanup.cs` | Disk cleanup |
| `source/mod-manager.template.html` | The page shown in the window |
| `source/build.ps1` | Build script |
| `source/make-icon.ps1` | Draws the icon, a grass block (`app.ico`) |
