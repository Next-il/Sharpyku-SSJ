# Sharpyku-SSJ - Strafe Sync Jump Plugin for CS2

A **Strafe Sync Jump (SSJ)** plugin for CS2 servers running [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp). Shows per-jump strafe statistics on a HUD card (or in chat), helping players improve their bunny hopping technique.

Next-il fork of [Sharpyku/Sharpyku-SSJ](https://github.com/Sharpyku/Sharpyku-SSJ): the stats and the `!ssj` settings are Panorama HUD panels ([PanoramaManager](https://www.nuget.org/packages/PanoramaManager)) in the same family as our SharpTimer HUD, and CS2MenuManager is gone.

![CS2](https://img.shields.io/badge/CS2-Plugin-blue)
![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

- **Per-jump stats** - Pre speed, speed, gain %, sync %, strafe count
- **HUD card** - one row per jump of the run, PRE, average sync and speed gained; chat on request
- **Clickable settings** - `!ssj` opens a mouse menu; every change shows on the card behind it
- **Accurate sync** - Velocity-based cross/dot product sync algorithm
- **Autobhop support** - Detects in-air re-jumps (same-tick land+jump)
- **Fully configurable** - Auto-generated `config.json` with all settings (tag, colors, database, defaults, tuning, HUD defaults)
- **Startzone integration** - Only tracks jumps after leaving the start zone (requires [SharpTimer](https://github.com/DEAFPS/SharpTimer))
- **Per-player settings** - SSJ on/off, where the stats go, repeat mode, jumps per run (1-10), card spot, size, accent and hold time
- **MySQL/MariaDB persistence** - Player settings saved to database, persist across server restarts

## HUD

One card, left of the crosshair by default, away from every default spot of the SharpTimer HUD:

```
SSJ                    PRE 276
#  SPEED   GAIN  SYNC  STRAFES
1    302  +9.4%   88%        4
2    331  +9.6%   85%        5
3    357  +7.9%   81%        5
...
AVG SYNC 85%         GAINED +81
```

- A run's card appears at its first takeoff with PRE and one empty row per jump (as many rows as the player's jumps setting), and each row fills as its jump lands. The latest row is highlighted.
- Gain is green / yellow / red for up / flat / down; sync is green 80+, yellow 60+, gold 40+, red under 40, as in chat.
- After the run's last jump (or when the chain breaks) the card stays for the player's hold time: 3s, 6s (default), 10s or until the next run.
- While spectating, the card shows the watched player's run with their name in the head.
- A player the HUD cannot reach gets the chat line instead, whatever they picked.

The layouts are `hud/panorama/layout/custom_game/ssj_hud.xml` and `ssj_menu.xml`, with `hud/panorama/styles/custom_game/ssj_hud.css`, `ssj_menu.css` and the shared `hud_shared.css`. They ship in the workshop addon (the plugin asks for the compiled `ssj_hud.vxml_c` / `ssj_menu.vxml_c`), not in the plugin zip.

## Chat Output

![SSJ Chat Output](images/ssj_chat.png)

Off by default with the HUD; a player picks CHAT or BOTH in `!ssj` (or `!ssj chat`) to get it back.

```
[SSJ] Jump 1 | Pre: 340 | Speed: 412 | Gain: +21.2% | Sync: 87% | Strafes: 4
[SSJ] Jump 2 | Speed: 478 | Gain: +16.0% | Sync: 82% | Strafes: 5
[SSJ] Jump 3 | Speed: 531 | Gain: +11.1% | Sync: 74% | Strafes: 6
```

- **Pre** - Horizontal speed at the start of the first jump
- **Speed** - Horizontal speed at landing
- **Gain** - Speed gained during the jump (%)
- **Sync** - Percentage of air ticks where turn direction matched strafe acceleration
- **Strafes** - Number of strafe direction changes during the jump

Colors: Sync ≥80% = green, ≥60% = yellow, ≥40% = gold, <40% = red

## Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) v1.0.374+ (PanoramaManager's floor)
- The workshop addon with the SSJ layouts, for the HUD (without it, players get chat)
- [SharpTimer](https://github.com/DEAFPS/SharpTimer) - for startzone detection (optional - without it, SSJ tracks all jumps)
- **MySQL or MariaDB** - for saving player settings (optional - without it, settings reset on reconnect)

## Installation

1. Download the latest release from [Releases](../../releases)
2. Extract the `SSJ-Plugin` folder to:
   ```
   csgo/addons/counterstrikesharp/plugins/SSJ-Plugin/
   ```
3. Make sure `SSJ-Plugin.dll`, `MySqlConnector.dll` and `PanoramaManager.dll` are all in the folder
4. Start/restart your server - a `config.json` will be auto-generated
5. Edit the config to your liking (see below)
6. Upgrading a server with an existing `ssj_settings` table: run `ssj-hud-settings.sql` once (adds the `Hud` column)

### File Structure
```
csgo/addons/counterstrikesharp/
├── plugins/
│   └── SSJ-Plugin/
│       ├── SSJ-Plugin.dll
│       ├── MySqlConnector.dll
│       └── PanoramaManager.dll
└── configs/
    └── plugins/
        └── SSJ-Plugin/
            └── config.json         ← auto-created on first load
```

## Configuration

On first server start, the plugin auto-generates a config file at:
```
csgo/addons/counterstrikesharp/configs/plugins/SSJ-Plugin/config.json
```

An existing `config.json` is only read, never rewritten; a config without the `Hud` section gets its defaults.

```json
{
  "ChatTag": "[SSJ]",
  "ChatTagColor": "DarkBlue",
  "StartzoneOnly": true,
  "DefaultMaxJumps": 6,
  "DefaultEnabled": true,
  "DefaultRepeat": true,
  "MinAirTicksToReport": 2,
  "PrintThrottleTicks": 8,
  "GroundSettleTicksAir": 3,
  "ChainResetGroundTicks": 24,
  "Database": {
    "Enabled": false,
    "Host": "localhost",
    "Port": 3306,
    "Database": "BhopServer",
    "User": "root",
    "Password": "",
    "TableName": "ssj_settings"
  },
  "Hud": {
    "Enabled": true,
    "DefaultOutput": "hud",
    "DefaultSpot": "xl",
    "DefaultSize": "m",
    "DefaultHoldSeconds": 6,
    "DefaultAccent": "ice"
  }
}
```

### Config Options

| Option | Default | Description |
|--------|---------|-------------|
| `ChatTag` | `[SSJ]` | The tag shown in chat before each message. Change to anything you want (e.g. `[BHOP]`, `[EG]`) |
| `ChatTagColor` | `DarkBlue` | Color of the tag. Options: `DarkBlue`, `Blue`, `LightBlue`, `Red`, `DarkRed`, `Green`, `Lime`, `Gold`, `Yellow`, `Purple`, `Magenta`, `Olive`, `Orange`, `Grey`, `LightRed`, `LightPurple` |
| `StartzoneOnly` | `true` | Only track SSJ after player leaves the start zone. Set to `false` to track all jumps anywhere |
| `DefaultMaxJumps` | `6` | Default number of jumps to display per run (players can override via `!ssj`) |
| `DefaultEnabled` | `true` | Whether SSJ is enabled by default for new players |
| `DefaultRepeat` | `true` | Whether stats show every run by default (off: the first run only, until `!ssj reset`) |
| `MinAirTicksToReport` | `2` | Minimum air ticks before a jump is reported (filters micro-jumps) |
| `PrintThrottleTicks` | `8` | Minimum ticks between two reported jumps (prevents spam) |
| `GroundSettleTicksAir` | `3` | Ticks in air before takeoff is confirmed |
| `ChainResetGroundTicks` | `24` | Ticks on ground before bhop chain resets |

### HUD Settings

| Option | Default | Description |
|--------|---------|-------------|
| `Hud.Enabled` | `true` | The HUD card and the clickable `!ssj` menu. `false` = chat only, `!ssj` takes chat arguments. Takes effect on a plugin load |
| `Hud.DefaultOutput` | `hud` | Where a player's stats go until they pick: `hud`, `chat` or `both` |
| `Hud.DefaultSpot` | `xl` | `xl` left of the crosshair, `xr` right of it, `xa` above it, `tc` top center, `rm` right side, `br` next to the timer card |
| `Hud.DefaultSize` | `m` | `s`, `m` or `l` |
| `Hud.DefaultHoldSeconds` | `6` | Seconds the card stays after a run's last jump: `3`, `6`, `10`, or `0` for until the next run |
| `Hud.DefaultAccent` | `ice` | `ice`, `orange`, `pink`, `lime` or `white` (the SharpTimer HUD's accents) |

### Database Settings

| Option | Default | Description |
|--------|---------|-------------|
| `Database.Enabled` | `false` | Set to `true` to enable MySQL/MariaDB persistence |
| `Database.Host` | `localhost` | Database server address |
| `Database.Port` | `3306` | Database server port |
| `Database.Database` | `BhopServer` | Database name |
| `Database.User` | `root` | Database username |
| `Database.Password` | `""` | Database password |
| `Database.TableName` | `ssj_settings` | Name of the settings table |

The plugin creates the table on first connect. Player settings (Enabled, Repeat, MaxJumps, and the HUD choices in `Hud`) are saved per SteamID, 2 seconds after the last change and on disconnect.

`Hud` holds only what differs from the `Hud` defaults above (for example `o=both;p=xr;s=l`), so a default changed in the config reaches everyone who never picked that setting. An existing table does not get the column by itself: run `ssj-hud-settings.sql`. Until then the plugin logs one warning and the HUD choices last until the player leaves.

> **No database?** Leave `Database.Enabled` as `false` - the plugin works fine without it, settings just won't persist between sessions.

## Commands

| Command | Description |
|---------|-------------|
| `!ssj` | Open the SSJ settings (chat status and usage when the HUD is off) |
| `!ssj on` / `!ssj off` | SSJ on or off |
| `!ssj 1`-`!ssj 10` | Jumps per run |
| `!ssj hud` / `!ssj chat` / `!ssj both` | Where the stats go |
| `!ssj repeat` | Every run on or off |
| `!ssj reset` | Reset the current run's stats (and show again with repeat off) |

### Settings Menu

A clickable panel (the mouse is free while it is open), in the SharpTimer `!hud` panel's look:

- **SSJ** - on / off
- **Show in** - HUD, chat, or both
- **Jumps** - 1 to 10 per run
- **Every run** - on, or the first run only until reset
- **Keep it up** - how long the card stays after the last jump: 3s, 6s, 10s, always
- **Accent** - the timer HUD's five
- **Where it goes** - a mini map of the screen: click a spot. CS2's HUD and the timer HUD's default spots are drawn grey
- **Size** - S, M, L
- **Reset stats** / **Defaults**

## How Sync Works

The plugin uses a **velocity-based cross/dot product** algorithm:

1. Each tick, it takes the player's horizontal velocity vector `(vx, vy)`
2. Computes the **rotation angle** between current and previous velocity using:
   - `dot = prev·curr` (cosine of angle)
   - `cross = prev×curr` (sine of angle)
   - `angle = atan2(cross, dot)`
3. Projects the **acceleration vector** onto the perpendicular axis of the heading direction
4. If the turn direction matches the strafe acceleration direction → that tick counts as **synced**
5. `Sync% = (synced ticks / total air ticks) × 100`

This is more accurate than yaw-based methods because it measures actual velocity changes rather than just view angles.

## Building from Source

```bash
dotnet build -c Release
```

Validate the layouts before they go into the workshop addon:

```bash
python3 -I path/to/cs2-panorama-hud/scripts/validate.py hud/panorama
```

### Dependencies (NuGet)
- `CounterStrikeSharp.API` 1.0.374
- `MySqlConnector` 2.4.0
- `PanoramaManager` 0.4.3 (copied next to the plugin on build)

### Shared API References
The `.csproj` references `SharpTimerAPI.dll` from `lib/` (compile only; loaded from `shared/` at runtime).

## Credits

- Built on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) by roflmuffin
- Timer integration via [SharpTimer](https://github.com/DEAFPS/SharpTimer) API
- Original plugin and menu by [Sharpyku](https://github.com/Sharpyku/Sharpyku-SSJ)

## License

[MIT](LICENSE)
