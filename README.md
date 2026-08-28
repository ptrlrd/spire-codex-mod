<p align="center">
  <a href="https://spire-codex.com">
    <img src="https://spire-codex.com/spire-codex-white-silent-black-background.png" alt="Spire Codex" width="200" />
  </a>
</p>

# Spire Codex — Slay the Spire 2 mod

The in-game companion for **[spire-codex.com](https://spire-codex.com)**. It connects Slay the
Spire 2 to the Spire Codex ecosystem: automatic run tracking and a native companion panel, using
the same data and API as the [website](https://github.com/ptrlrd/spire-codex) and the Overwolf
overlay.

- **Run tracking** — uploads your finished runs so they land on [spire-codex.com](https://spire-codex.com)
  and the leaderboards automatically. Off by default; you opt in.
- **In-game companion (F5)** — a draggable panel with your live run, leaderboards, your recent
  runs, and an About tab. Plus on-map guidance: the recommended route with community danger,
  upcoming events, win-rate plates on card rewards and shops, and community stats inside the
  game's own tooltips.
- **Overwolf Overlay** — pairs perfectly with the Overwolf overlay. If you haven't downloaded it yet, get it at [spire-codex.com/overlay](https://www.overwolf.com/app/ptrlrd-spire_codex)

## Install

1. Install [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) (the required dependency)
   into the game's `mods/` folder (paths below).
2. Download the latest release and extract it into the same `mods/` folder, so you end up with
   `mods/SpireCodex/` next to `mods/BaseLib/`.
3. Launch the game and press **F5** or **L3/R3** on your controller.

## Build
### Prerequisites
- Godot 4.5.1 .NET or equivalent - e.g. MegaCrit's customised [MegaDot](https://megadot.megacrit.com/).
- The [.NET SDK](https://dotnet.microsoft.com/en-us/download) (9.0 or higher)

Note: On Windows, the scripts under `tools/` are configured to run WSL, but still target your Windows installation of .NET, etc.


### Runtime prequisites:
- A local installation of Slay the Spire 2 install (for `sts2.dll`)
- BaseLib, installed [manaually](#install) or [via Steam Workshop](https://steamcommunity.com/workshop/filedetails/?id=3737335127)
  - Note: You can check to see if you have it in your [Workshop Mods](#Workshop Mods) 
### Instructions

1. Copy `Directory.Build.props.example` to `Directory.Build.props`
    2. Set the path to your Godot executable, e.g.: 
   
       `~/Downloads/megadot-4.5.1-m.14-linux-x86_64-editor-csharp/MegaDot_v4.5.1-stable_mono_linux.x86_64`
    3. If you installed STS2 or BaseLib into a non-default [Steam Library](#Steam%20Library) location, set additional overrides in `Directory.Build.prop` sas needed. See [Sts2PathDiscovery.props](./Sts2PathDiscovery.props) for available props.
2. Run a dotnet build:
  
    `dotnet build SpireCodex.csproj -c Debug`
3. On the first build (and when updating assets), you will also need to publish:
   
    `dotnet publish SpireCodex.csproj -c Debug`

Note: The [ModTemplate-StS2 wiki](https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup) also has additional information that may be useful if you run into issues, though this project is not setup identically.

## Links

- Website: **[spire-codex.com](https://spire-codex.com)**
- Main project & API: **[github.com/ptrlrd/spire-codex](https://github.com/ptrlrd/spire-codex)**
- Discord: [discord.gg/uged4qFufK](https://discord.gg/uged4qFufK)
- Support: [Patreon](https://www.patreon.com/cw/SpireCodex)

## Credits
- The "Import vanilla saves" button is based on
[ImportVanillaSaves](https://github.com/Ind-E/ImportVanillaSaves) by Ind-E, used with permission.
- The project was built with the [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2) starter template.

Full credits and licenses: [THIRD-PARTY.md](THIRD-PARTY.md).

## Useful Directory/Folder Information
## Steam Libraries
A Steam Library generally maps to a `steamapps/` folder and is where games and mods are installed. You can have multiple Steam Libraries in custom locations, but the defaults are as follows:

| OS | `steamapps/` folder |
|----|----------------|
| Windows | `C:\Program Files (x86)\Steam\steamapps` |
| Linux | `~/.steam/steam/steamapps` |
| Linux (Snap) | `~/snap/steam/common/.steam/steam/steamapps` |
| macOS | `~/Library/"Application Support"/Steam/steamapps/common/"Slay the Spire 2"/SlayTheSpire2.app/Contents/MacOS/mods/`|

## Workshop Mods
Mods you install via the Steam Workshop will be installed under `steamapps/workshop/content` in folders with numeric names up to two levels deep, e.g.:
`C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840\3737335127\BaseLib`

Generally you don't need to touch these directly but it can help to check that they are present.

## Local Mods
The `mods/` folder contains manually installed mods for local development. Mods found in this folder will override matching workshop mods
The `mods/` folder lives inside your Slay the Spire 2 install. The location varies depending on your OS:

| OS | `mods/` folder |
|----|----------------|
| Windows | `steamapps\common\Slay the Spire 2\mods\` |
| Linux | `steamapps/common/Slay the Spire 2/mods/` |
| macOS | `steamapps/common/"Slay the Spire 2"/SlayTheSpire2.app/Contents/MacOS/mods/`|