# Third-party credits

Work by other people that Spire Codex builds on. Anything with a license below is reproduced
because that license asks for it.

## ImportVanillaSaves

The Settings tab's "Import vanilla saves" is based on the approach worked out by
**[ImportVanillaSaves](https://github.com/Ind-E/ImportVanillaSaves)** by **Ind-E**, used with
permission.

Slay the Spire 2 keeps modded progress in its own `modded/` save tree, and copying the files by
hand does not survive the game's Steam Cloud sync. Ind-E's mod is where the working method comes
from: turn `UserDataPathProvider.IsRunningModded` off, let the game's own save managers load the
vanilla profile, turn it back on, then save, so the writes go through the game's cloud-backed save
store. The implementation in `Code/Core/SaveImport.cs` is our own and differs in a couple of
places (the flag is assigned rather than Harmony-patched, and the read half avoids
`SwitchProfileId` so the vanilla files are never written), but the idea is theirs.

Licensed MIT:

```
MIT License

Copyright (c) 2026 Indi

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## ModTemplate-StS2

The project skeleton (`Godot.NET.Sdk` wiring, Harmony and BaseLib setup, the packaging scripts)
started from **[ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2)** by **Alchyr**.

## BaseLib

Mod settings registration and the in-game options menu integration use
**[Alchyr.Sts2.BaseLib](https://www.nuget.org/packages/Alchyr.Sts2.BaseLib)** by **Alchyr**.

## Steamworks.NET

Steam ticket authentication uses **[Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET)**
by Riley Labrecque, licensed MIT.
