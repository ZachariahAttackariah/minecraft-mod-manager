# Changelog

What changed in Minecraft Mod Manager, newest first. The app shows this under Settings → Changelog,
and after an update it offers what's new since the last time it was opened.

## 2026-11-09
- Changelog under Settings, and a "What's new" note the first time the app opens after an update.
- Tag menu: ticking a tag no longer scrolls the list; ticked tags move to the top only the next time the menu opens.
- Remote access: use the app from a phone or another computer on your home network, or from anywhere through a private VPN such as Tailscale. Off until switched on in Settings; each device is paired once with a code shown on the PC.
- Phones: the section tabs in one row you swipe, side panels start folded, finger-sized menus, hold a row for its menu, and nothing wider than the screen.
- Dropping files on the window refreshes the list again afterwards.
- Phones, made for a tall touch screen: the section tabs sit in a bar along the bottom (Mods, Packs, Configs, Server, and More for the rest), menus, pickers and dialogs come up from the bottom with big rows and full-width buttons, the title gives way to the list, switches, buttons and checkboxes are larger, and the selection bar is two short lines. Keyboard hints and Quit are left off phones; a tap beside a menu just closes it.
- Shared → Game settings: every game setting in one place, grouped like the game's own menus (video, sound, controls, chat, accessibility, skin, language, online and more), with sliders, switches and lists, a search box and a reset to the game's default on each. Mods' own settings in options.txt (their sound volumes and switches) are there too. Saving writes them to every instance that shares its game settings.
- Game settings also has the video pages mods add to the game's own menu: Sodium (quality, performance, advanced), Sodium Extra, More Culling and Iris's shader options, saved into those mods' own config files in each sharing instance.
- Configs: every setting is laid out like Game settings, in folding boxes per section, with sliders where a mod gives a range, a reset to the mod's default on each, and the same save bar. Opening the app straight onto Configs shows the file list again.
- Server › Settings in the same style: every line of server.properties named and grouped (joining & access, gameplay, world, performance, resource pack, remote control), with switches, sliders, lists, search and resets, plus memory, restarts and backups in their own sections.
- One look for every settings page: Settings, the Shared tab's lists (switches instead of ticks), New instance and instance settings, Create server, a mod's update and pin options, and Server › Mods & Configs use the same boxes and rows.
- Tabs with two words are in title case (Key Binds, Resource Packs, Game Settings, Mods & Configs…).
- More looks: Copper, Prismarine and Cherry accents; a shade for the dark theme (Charcoal, Deepslate, Midnight, Spruce, Black) and for the light one (Snow, Birch, Stone); glass panels; a choice of font; high contrast; and Ocean and Cherry Grove quick looks.
- Text sizes above 120% save again.
- Look settings show a preview of every choice: theme, shades, tags, text size, background, how strongly your picture shows, corners, panels, font, contrast, row spacing, animation, pixel font, rows, row buttons and tags per row are cards you can compare side by side, and Mod List › Show has a sample row that follows the columns you pick.
- Smoother: folding sections open and close instead of snapping, right-click menus ease in, menus and dialogs slide up on a phone, save bars rise in, buttons and cards answer a press, ticks pop in, highlights fade and the tab underline grows in. All quick, and all off with Animation › Reduce.
- On a phone, opening a menu, picker or dialog no longer pops up the keyboard; it comes up when you tap into a text box.
- On a phone, the Server tab (and Shared › Shader Packs) no longer zooms the whole app out: a long row of tabs or a long pack name could make the page wider than the screen. Tapping a text box no longer zooms an iPhone in either.
- Phone and desktop layout pass: sub-tabs (Shared, Packs, Features, Server) are one row you swipe on a phone, a list's "N of M shown" sits top right, search boxes drop the "/" hint on touch screens, small buttons and sort headers are finger-sized, and Cleanup's bar matches the save bars.
- Settings on a phone: the sections in one row you swipe, quick looks filling the width, and rows of buttons (like the shades) under their name instead of squeezing it.
- Selecting no longer nudges anything: the list stays exactly where it is, and every selection bar keeps its height (its buttons wait greyed out until something is picked).

## 2026-11-08
- Shared tab: mods, resource packs, shader packs and data packs kept once and copied into every ticked instance; game settings, configs and Java arguments shared per instance from the same tab.
- Shared tab: Add mods / Add packs searches Modrinth and CurseForge and installs straight into Shared, dependencies included.
- Shader settings follow their shader pack across instances; the newest copy wins.
- The old linked pack folders are converted to real folders in each instance, and their packs move into Shared.
- Every instance gets its own tab, with its mods managed like the main list.
- Copy, cut and paste mods and packs between instances.
- Packs: on/off switches, Copy / Cut / Paste, Share, Show in Shared and Open on Modrinth in the right-click menu.
- Brighter, more saturated colours; each tag keeps the same colour from one start to the next.
- Clearer wording throughout: Dependencies and Dependants, "None selected", shorter hints.
- A calmer look: flat buttons, normal-case tabs and labels, softer corners. The pixel font stays on the title, PLAY and dialog titles.
- Settings: six sections listed down the left, one-line descriptions, and the list columns in one "Show" row.
- Layout and consistency pass across every tab.

## 2026-11-07
- Fabric toggle in Add mods, with Sinytra Connector installed automatically.
- Search boxes for tags, including in the right-click menu's tag list.
- The first page of every search is fetched in the background, so results show at once.
- The window scales per screen and with Text size.

## 2026-10-29
- New app icon: the chiseled bookshelf's front face.

## 2026-10-26
- Items, Recipes and Key binds pages.
- Features lists every mob, including ones that only spawn in structures or from code.

## 2026-10-25
- Features tab: structures, mobs, biomes and ores the mods add, with switches to turn them off and links to the mods' config settings.
- The mod list's tools on the other lists too.
- Tag menu: the selected mods' tags come first.
- Clicking off the mods clears the selection; a selected mod stays shown until it's deselected.

## 2026-10-21
- Pre-launch check reads what bundled mods require; guarded optional code no longer counts as a missing mod.

## 2026-10-18
- Server: mods kept on the server, a "Sync with game" button, and Add mods for the server.
- Crash reader offers to fix missing mods and only looks at the same launch.
- Edit a mod's configs right in its details window.
- A mod's note shows when the cursor rests on its row.
- Pre-launch check stops treating optional mods as required.

## 2026-10-17
- Deep pre-launch check, and a crash reader that finds the real report and weighs the evidence.
- The launcher stays out of sight; the app follows the game instead.

## 2026-10-16
- Config editor: every mod's settings as a page.
- Automatic config syncing between instances, and from the game to the server.
- New jars get their sides and library tags automatically.
- Search previews in the app, a Versions tab with changelogs, and installing a chosen version.
- Search and install packs from CurseForge too.
- Packs: selection boxes and bulk actions, like the mod list.

## 2026-10-15
- Server page makeover: header bar, Mods & configs, Players and Backups tabs.
- Turning a mod on also turns on what it depends on.
- Mods and Libraries keep their own search and filters.

## 2026-10-14
- The game's pixel font for the title, PLAY and headings.
- Modrinth and CurseForge as tabs in the title bar, with ads blocked.
- Two-row toolbar: filters and sort on top, the selection and its tools below.
- Look options: list columns, tag colours, start page, background picture and your own user.css.
- A much faster mod list, instant switches and faster combined search.
- Updates are only checked with the Check for updates button.

## 2026-10-13
- CurseForge: recognise, update, search and install mods (needs your own API key).
- Settings page and one search list for both sites.
- Pre-launch check with Install it / Add all missing and version fixes.
- Detect reads the jars' code and contents to work out client and server sides.
- Modpack export and import (.mrpack and CurseForge zip).
- On/off presets, compare with another folder, drag and drop, command palette and shortcut sheet.
- World backups, scheduled server restarts and backups.
- Crash reader names the mods a crash points at.
- "What changed" on opening, search filters and a dependency tree.

## 2026-10-12
- Libraries tab: library-tagged mods move out of the Mods list.
- Gallery tab in the mod preview.

## 2026-10-11
- Packs section: resource packs, shader packs and data packs, with Iris and Paxi installed when needed.
- Shared settings between instances, version and loader switching.
- Create a server and switch between servers.
- Detect client/server sides, sort and filter by side.
- Mod icons, clickable tags and sides on rows.
- Tags from another tool's .mod-tags.json are shown live and never written to.

## 2026-10-10
- First version: the mod list with on/off switches and tags, a launcher-style look, the instance picker and instance management.
