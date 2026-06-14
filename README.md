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

## Install

1. Install [BaseLib](https://www.nexusmods.com/slaythespire2/mods/103) (the required dependency)
   into your `Slay the Spire 2/mods/` folder.
2. Download the latest release and extract it into the same `mods/` folder.
3. Launch the game and press **F5**.

## Links

- Website: **[spire-codex.com](https://spire-codex.com)**
- Main project & API: **[github.com/ptrlrd/spire-codex](https://github.com/ptrlrd/spire-codex)**
- Discord: [discord.gg/uged4qFufK](https://discord.gg/uged4qFufK)
- Support: [Patreon](https://www.patreon.com/cw/SpireCodex)

## Building

Built with the [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2) starter
(`Godot.NET.Sdk`, Harmony, BaseLib). Needs a local Slay the Spire 2 install (for `sts2.dll`), and
Godot 4.5.1 .NET to export the `.pck` that carries the readable settings labels.

- Copy `Directory.Build.props.example` to `Directory.Build.props` and set your Godot path.
- Build (installs into the game's mods folder): `dotnet build SpireCodex.csproj -c Debug`
- Package a release zip (dll + manifest + pck): `bash tools/package.sh`
