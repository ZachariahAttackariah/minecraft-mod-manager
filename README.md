# Minecraft Mod Manager

A Windows desktop app for managing a Minecraft mods folder and, optionally, a dedicated server that runs the same mods.

It's a small native window (WinForms + WebView2) with a local web page inside. The internal web server only listens on `localhost`, and only the app's own page can make changes.

## Features

- **Mods**
  - Turn mods on and off. Disabling renames `X.jar` to `X.jar.disabled`, the same as launchers do.
  - Delete mods to the Recycle Bin.
  - Select several mods at once, file-manager style.
  - Tag mods (client / server, plus your own topic tags), stored in `mod-tags.json` next to the jars.
- **Modrinth**
  - Recognises installed jars by their SHA-1 hash.
  - Checks for updates and shows every changelog between your version and the new one before updating.
  - Lets you switch to any older or newer version.
  - Searches and installs mods, together with the mods they require.
  - Checks every download against Modrinth's checksum.
- **Pre-launch check:** before Play (and before starting the server), the app reads every jar's `neoforge.mods.toml` / `mods.toml` / `fabric.mod.json`. It reports what would stop NeoForge from loading, with one-click fixes:
  - required mods that are missing or turned off;
  - library versions outside the required range;
  - the same mod twice;
  - mods made for another Minecraft version;
  - Fabric mods without Sinytra Connector.
- **Mod details**
  - Description, links and versions with changelogs.
  - What a mod needs and what needs it, including mods bundled inside jars.
  - Its config files, and whether the server's copies match.
- **Server**
  - Starts and stops a NeoForge dedicated server the way its `run.bat` would.
  - Live console with filters, command input and history.
  - Online players and connection info.
  - A `server.properties` editor.
  - Asks before closing the window while the server runs, then stops it so the world saves.
- **Sync from the game:** compares the game's mods (using their client/server tags) with the server's, and compares config files setting by setting. Nothing changes until you press Apply, and never while the server is running.
- **Snapshots**
  - Remembers every jar in the folder and whether it's on or off.
  - Keeps one copy of each jar, so a restore can bring back deleted or updated mods.
  - Taken automatically before bulk changes.
- **Find a problem mod:** a guided search that turns off half the suspects each round. Libraries stay on while something that's still on needs them, and everything is put back at the end.
- **Disk cleanup:** old logs, crash reports, Java crash dumps, debug output and Distant Horizons LOD data, all moved to the Recycle Bin.
- **Tabs:** links open as tabs in the same window. A `.jar` downloaded in any tab goes straight into the mods folder.
- **Instances and Play:** a launcher-style bottom bar has an instance picker on the left and a big PLAY button in the middle.
  - The picker lists every [Prism Launcher](https://prismlauncher.org/) instance with its Minecraft version, loader and mod count, plus other mods folders you've used.
  - Choosing an instance also sets the Minecraft version and loader used for Modrinth lookups.
  - PLAY launches the chosen instance through Prism.
  - Instances can be created (empty, or as a copy of another), duplicated, renamed, given more or less memory, moved to another loader version, and deleted (to the Recycle Bin). They are written in Prism's own format, and Prism downloads the game files on first launch.

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
| `source/Window.cs` | The window and its tabs |
| `source/Instances.cs` | The instance picker, and creating, changing and deleting Prism instances |
| `source/Server.cs` | Starting, stopping and talking to the Minecraft server |
| `source/Sync.cs` | Comparing and syncing the game and the server |
| `source/JarMeta.cs` | Reading jar metadata, and the pre-launch check |
| `source/Details.cs` | Mod details, versions and changelogs |
| `source/Snapshots.cs` | Snapshots and finding a problem mod |
| `source/Cleanup.cs` | Disk cleanup |
| `source/mod-manager.template.html` | The page shown in the window |
| `source/build.ps1` | Build script |
| `source/make-icon.ps1` | Draws the icon (`lodestone.ico`) |
