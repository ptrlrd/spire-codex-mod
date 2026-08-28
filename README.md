<p align="center">
  <a href="https://spire-codex.com">
    <img src="https://spire-codex.com/spire-codex-white-silent-black-background.png" alt="Spire Codex" width="200" />
  </a>
</p>

# Spire Codex — Slay the Spire 2 mod

The in-game companion for <a href="https://spire-codex.com" target="_blank">spire-codex.com</a>. It connects Slay the
Spire 2 to the Spire Codex ecosystem: automatic run tracking and a native companion panel, using
the same <a href="https://github.com/ptrlrd/spire-codex" target="_blank">data and API</a> as the website and the <a href="https://www.overwolf.com/app/ptrlrd-spire_codex" target="_blank">Overwolf
overlay</a>.

- **Run tracking** — uploads your finished runs so they land on <a href="https://spire-codex.com" target="_blank">spire-codex.com</a>
  and the leaderboards automatically. Off by default; you opt in.
- **In-game companion (F5)** — a draggable panel with your live run, leaderboards, your recent
  runs, and an About tab. Plus on-map guidance: the recommended route with community danger,
  upcoming events, win-rate plates on card rewards and shops, and community stats inside the
  game's own tooltips
- **Overwolf Overlay** — pairs perfectly with the Overwolf overlay. If you haven't downloaded it yet, get it at <a href="https://www.overwolf.com/app/ptrlrd-spire_codex" target="_blank">Overwolf
  overlay</a>

## Install

1. Install <a href="https://github.com/Alchyr/BaseLib-StS2" target="_blank">BaseLib</a> by <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127" target="_blank">subscribing on the Steam Workshop</a>
2. Download the <a href="https://github.com/ptrlrd/spire-codex-mod/releases/latest"  target="_blank">latest release</a> and extract it into the [Local Mods folder](#local-mods)
3. Launch the game and press **F5** or **L3/R3** on your controller

## Build
### Build Prerequisites
- Godot 4.5.1 .NET or equivalent - e.g. MegaCrit's customised <a href="https://megadot.megacrit.com">MegaDot</a>
- The <a href="https://dotnet.microsoft.com/en-us/download" target="_blank">.NET SDK</a> (9.0 or higher)

### Runtime prequisites:
- A local installation of Slay the Spire 2
- BaseLib, preferably <a href="https://steamcommunity.com/workshop/filedetails/?id=3737335127" target="_blank">via Steam Workshop</a>
  - Note: You can check to see if you already have it in your [Workshop Mods folder](#workshop-mods)

### Instructions

1. Copy `Directory.Build.props.example` to `Directory.Build.props` and set the appropriate paths
   1. The path to your Godot executable is mandatory, e.g.: 
   
       `~/Downloads/megadot-4.5.1-m.14-linux-x86_64-editor-csharp/MegaDot_v4.5.1-stable_mono_linux.x86_64`
   2. If you installed STS2 or BaseLib into a non-default [Steam Library](#steam-libraries) location, set additional overrides in `Directory.Build.props` sas needed. See <a href="./Sts2PathDiscovery.props" target="_blank">Sts2PathDiscovery.props</a> for available props.
2. Run a dotnet build:
  
    `dotnet build SpireCodex.csproj -c Debug`
3. On the first build (and when updating assets), you will also need to publish:
   
    `dotnet publish SpireCodex.csproj -c Debug`

Note: The <a href="https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup" target="_blank">ModTemplate-StS2 wiki</a> also has additional information that may be useful if you run into issues, though this project is not setup identically.

## Links

- Website: <a href="https://spire-codex.com" target="_blank">spire-codex.com</a>
- Main project & API: <a href="https://github.com/ptrlrd/spire-codex" target="_blank">github.com/ptrlrd/spire-codex</a>
- Discord: <a href="https://discord.gg/uged4qFufK" target="_blank">discord.gg/uged4qFufK</a>
- Support: <a href="https://www.patreon.com/cw/SpireCodex" target="_blank">Patreon</a>

## Credits
The "Import vanilla saves" button is based on <a href="https://github.com/Ind-E/ImportVanillaSaves" target="_blank">ImportVanillaSaves</a> by <a href="https://github.com/Ind-E" target="_blank">Ind-E</a>, used with permission.

Full credits and licenses: <a href="./THIRD-PARTY.md" target="_blank">THIRD-PARTY.md</a>.

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