# Red Alliance Speedrun Tools

BepInEx 5 plugin for **Red Alliance** (Unity 2019.4.40f1). Speedrun timers, restoration of the OLD-version wall-clip bug with geometry-aware selectivity, Speedrunmod% (forced VortexRun level paths with saves + Easy difficulty), in-game menu, console commands.

## Install (end users)

Grab the latest `RedAllianceSpeedrun_vX.Y.Z_install.zip` from [Releases](../../releases).

1. Install [BepInEx 5.x x64](https://github.com/BepInEx/BepInEx/releases) into your Red Alliance folder. Run the game once so BepInEx creates its directories.
2. Extract the install zip anywhere and double-click `install.bat`. It auto-detects the Steam path and copies the DLL into `BepInEx\plugins`.
3. Launch the game.

Config file appears at `BepInEx\config\redalliance.speedrun.v2.cfg` after first run. Edit it, OR press **F10** in-game for the menu, OR type `racfg help` in the developer console.

## Features

- **RTA + IGT timers** on screen. IGT pauses during loads + pause menu.
- **TAB quick-restart** to any configured scene.
- **InstantUncrouchPatch** + **ClipPenetrationPushPatch** — reproduces the OLD-version wall-clip mechanic. Selective by geometry (velocity into wall = clip, parallel = no-op).
- **Speedrunmod%** — forces `VortexRun=true` (fast level paths) with `VortexLegacyMode=false` (saves work). Restart starts at `prologue_1` with timers at 0. `prison_1` cutscene auto-skips to `prison_2`. Forces `Easy` difficulty (patches the VortexRun 3x damage hardcode). Manual save anywhere via F5. Autosaves blocked.
- **PracticeMode** — allows `desu` cheats freely for training.
- **In-game F10 menu** — mode selector, level launcher (all scenes from build settings), config editor.
- **`racfg` console commands** — `list`, `get`, `set`, `save`, `reload`.
- **End-of-run dump** — config snapshot + FNV-1a hash, written to BepInEx log AND game console. Lets moderators verify run legitimacy.

## Default hotkeys

| Key | Action |
|---|---|
| TAB | Restart run |
| F5 | Force save (SpeedrunMode only) |
| F8 | Toggle FPSWalker.AllowClip |
| F10 | Open in-game menu |
| F11 | Diagnostics dump |
| Q | Swap target FPS between StandartFPS and MinFPS |

## Build from source

Requires .NET SDK (6+).

```
dotnet build -c Release RedAllianceSpeedrun.csproj
```

Output: `bin\Release\RedAllianceSpeedrun.dll`. Or run `build.bat` from the source release bundle.

Edit `<GameDir>` and `<BepInExCoreDir>` at the top of `RedAllianceSpeedrun.csproj` to match your machine.

## Project layout

| File | Role |
|---|---|
| `Plugin.cs` | BepInPlugin entry, config binding, Update/OnGUI, timers, restart, end-of-run dump |
| `SpeedrunTimer.cs` | RTA/IGT timer with pause-during-load + pause-during-menu options |
| `CrouchControllerBypassPatch.cs` | Zero `curHitCrouchColliders` after CrouchController |
| `InstantUncrouchPatch.cs` | Same-frame snap of capsule on lag-uncrouch (clip trigger) |
| `ClipPenetrationPushPatch.cs` | Iterative push-along-velocity, emulates Unity 2017 CC pen response |
| `PostClipBhopPatch.cs` | Optional curBhopBuffer hold during catch-up (skip friction) |
| `PostClipAcceleratePatch.cs` | Optional Quake-style positive-num2 clamp |
| `SpeedrunModePatches.cs` | VortexRun forcing, prison_1→prison_2 redirect, save UI fix, damage cap, autosave block |
| `ConsoleConfigCommandPatch.cs` | `racfg` console command for runtime config edits |
| `RaMenu.cs` | F10 in-game IMGUI menu |
| `Polyfills.cs` | Tuple deconstruction / `out var` polyfills for net46 |

## License

MIT (see LICENSE).
