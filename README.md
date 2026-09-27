<p align="center">
  <a href="https://spire-codex.com">
    <img src="https://spire-codex.com/spire-codex-white-silent-black-background.png" alt="Spire Codex" width="200" />
  </a>
</p>

# Spire Codex — Slay the Spire 2 mod

The in-game companion for [spire-codex.com](https://spire-codex.com). It connects Slay the
Spire 2 to the Spire Codex ecosystem: automatic run tracking and a native companion panel, using
the same [data and API](https://github.com/ptrlrd/spire-codex) as the website and the [Overwolf
overlay](https://www.overwolf.com/app/ptrlrd-spire_codex).

- **Run tracking** — uploads your finished runs so they land on [spire-codex.com](https://spire-codex.com)
  and the leaderboards automatically. Off by default; you opt in.
- **In-game companion (F5)** — a draggable panel with your live run, leaderboards, your recent
  runs, and an About tab. Plus on-map guidance: the recommended route with community danger,
  upcoming events, win-rate plates on card rewards and shops, and community stats inside the
  game's own tooltips
- **Run replays** — every run you upload also records what you did, decision by
  decision, so the run page can play it back. Recording is local until you turn
  uploads on.
- **Overwolf Overlay** — pairs perfectly with the Overwolf overlay. If you haven't downloaded it yet, get it at [Overwolf overlay](https://www.overwolf.com/app/ptrlrd-spire_codex)

## Install
You can subscribe to [Spire Codex on the Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3747536911), or you can manually install a specific release:
1. Subscribe to [BaseLib on the Steam Workshop](https://steamcommunity.com/workshop/filedetails/?id=3737335127) and make sure Steam installs it into your [Workshop Mods folder](#workshop-mods)
2. Download the desired [release](https://github.com/ptrlrd/spire-codex-mod/releases) and extract it into the [local Mods folder](#local-mods)
3. Launch the game and press **F5** or **L3/R3** on your controller

## Build
### Build Prerequisites
- Godot 4.5.1 .NET or equivalent - e.g. MegaCrit's customised [MegaDot](https://megadot.megacrit.com)
- The [.NET SDK](https://dotnet.microsoft.com/en-us/download) (9.0 or higher)

### Runtime prequisites:
- A local installation of Slay the Spire 2
- BaseLib, preferably by subscribing to [BaseLib on the Steam Workshop](https://steamcommunity.com/workshop/filedetails/?id=3737335127)
  - Note: You can check to see if you already have it in your [Workshop folder](#workshop-mods)

### Instructions

1. Copy `Directory.Build.props.example` to `Directory.Build.props` and set the appropriate paths
   1. The path to your Godot executable is mandatory, e.g.: 
   
       `~/Downloads/megadot-4.5.1-m.14-linux-x86_64-editor-csharp/MegaDot_v4.5.1-stable_mono_linux.x86_64`
   2. If you installed STS2 or BaseLib into a non-default [Steam Library](#steam-libraries) location, set additional overrides in `Directory.Build.props` as needed. See [Sts2PathDiscovery.props](./Sts2PathDiscovery.props) for available props.
2. Run a dotnet build:
  
    `dotnet build SpireCodex.csproj -c Debug`
3. On the first build (and when updating assets), you will also need to publish:
   
    `dotnet publish SpireCodex.csproj -c Debug`

### Packaging a release zip

`tools/package.sh` builds the dll, exports the `.pck` through Godot and writes a zip a player
can extract straight into their `mods/` folder.

    bash tools/package.sh

Run it from **WSL** on Windows. The script assumes it, and Git Bash resolves `python3` to the
Microsoft Store stub on a normal Windows Python install. Native Linux and macOS are fine.

It needs Godot 4.5.1 .NET (or MegaDot) installed with `GodotPath` set in
`Directory.Build.props`. A plain `dotnet build` does not, so you only hit this when packaging.

Close the game first: the publish step also copies into your live `mods/` folder.

Note: The [ModTemplate-StS2 wiki](https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup) also has additional information that may be useful if you run into issues, though this project is not setup identically.

## Links

- Website: [spire-codex.com](https://spire-codex.com)
- Main project & API: [github.com/ptrlrd/spire-codex](https://github.com/ptrlrd/spire-codex)
- Discord: [discord.gg/uged4qFufK](https://discord.gg/uged4qFufK)
- Support: [Patreon](https://www.patreon.com/cw/SpireCodex)

## Credits
The "Import vanilla saves" button is based on [ImportVanillaSaves](https://github.com/Ind-E/ImportVanillaSaves) by [Ind-E](https://github.com/Ind-E), used with permission.

For full credits and licenses see [THIRD-PARTY.md](./THIRD-PARTY.md).

## Useful Directory/Folder Information
### Steam Libraries
A Steam Library generally maps to a `steamapps/` folder and is where games and mods are installed. You can have multiple Steam Libraries in custom locations, but the defaults are as follows:

| OS | `steamapps/` folder |
|----|----------------|
| Windows | `C:\Program Files (x86)\Steam\steamapps` |
| Linux | `~/.steam/steam/steamapps` |
| Linux (Snap) | `~/snap/steam/common/.steam/steam/steamapps` |
| macOS | `~/Library/"Application Support"/Steam/steamapps` |

### Workshop Mods
Mods you install via the Steam Workshop will be installed under `steamapps/workshop/content` in folders with numeric names up to two levels deep, e.g.:
`C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840\3737335127\BaseLib`

Generally you don't need to touch these directly but it can help to check that they are present.

### Local Mods
The `mods/` folder contains manually installed mods for local development. Mods found in this folder will override matching workshop mods
The `mods/` folder lives inside your Slay the Spire 2 install. The location varies depending on your OS:

| OS | `mods/` folder |
|----|----------------|
| Windows | `steamapps\common\Slay the Spire 2\mods\` |
| Linux | `steamapps/common/Slay the Spire 2/mods/` |
| macOS | `steamapps/common/"Slay the Spire 2"/SlayTheSpire2.app/Contents/MacOS/mods/`|
