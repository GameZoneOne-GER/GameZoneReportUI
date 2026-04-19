# GameZoneReportUI

An in-game player report system for Rust (Oxide/uMod) with a full CUI form, Discord webhook integration, and an optional environment snapshot.

## Screenshots

> Replace with actual screenshots — upload to `screenshots/` in the repo and update the paths below.

| Report form | Discord embed |
|---|---|
| ![Report form](https://pic.gamezoneone.de/api/media/lxbsb6sw.png) | ![Discord embed](https://placehold.co/480x270/0d1117/4d9375?text=Discord+Embed) |

## Features

- **CUI Form** — clean in-game UI with category dropdown, player selector, evidence links and description field
- **Discord Webhook** — reports are posted as rich embeds to your Discord channel
- **Environment Snapshot** — captures nearby players (with inventory), vehicles and mounts at the time of the report
- **Admin History** — admins can review recent reports in-game via `/reportadmin`
- **Cooldown** — configurable cooldown between reports per player
- **Permission-based** — `gamezonereportui.use` (auto-granted to the `default` group) and `gamezonereportui.admin`
- **Optional Collector** — send reports to a custom HTTP endpoint (e.g. a logging backend)

## Installation

1. Copy `PlayerReportUI.cs` into your `oxide/plugins/` folder
2. Oxide will compile and load the plugin automatically
3. Edit `oxide/config/PlayerReportUI.json` and set your Discord Webhook URL

## Configuration

| Key | Default | Description |
|-----|---------|-------------|
| `Open Command` | `report` | Chat command to open the report form |
| `Discord Webhook URL` | *(empty)* | Webhook URL — leave empty to disable Discord |
| `Optional: Collector URL` | *(empty)* | HTTP endpoint for custom report forwarding |
| `Optional: Collector API Key` | *(empty)* | API key sent as `x-api-key` header |
| `Optional: Server ID` | *(empty)* | Identifier included in collector payloads |
| `Optional: Instance Name` | *(empty)* | Human-readable server name for collector payloads |
| `Send Collector Event` | `true` | Enable/disable collector forwarding |
| `Report Categories` | See config | List of selectable report reasons |
| `Players Per Page` | `6` | Player buttons per page in the player selector |
| `Minimum Description Length` | `15` | Minimum characters required in the description |
| `Cooldown Seconds` | `120` | Seconds between reports per player |
| `Max Description Characters` | `500` | Character limit for the description field |
| `Max Evidence URL Characters` | `600` | Character limit for the evidence links field |
| `Enable Surrounding Snapshot` | `true` | Capture environment data on submit |
| `Snapshot Radius (meters)` | `50` | Radius for the surrounding snapshot |
| `Save Snapshot as JSON File` | `true` | Persist snapshot to `oxide/data/GameZoneReportUI/snapshots/` |
| `Require Target Player` | `false` | Force selection of a reported player |
| `Auto-Grant Use Permission` | `true` | Automatically grant `gamezonereportui.use` to the `default` group |
| `Admin: Disable Report List` | `false` | Disable `/reportadmin` and history tracking |
| `Admin: Max History Entries` | `40` | Maximum entries kept in report history |
| `History In-Memory Only` | `false` | Keep history in RAM only (no oxide/data file) |

## Permissions

| Permission | Description |
|-----------|-------------|
| `gamezonereportui.use` | Open the report form (`/report`) |
| `gamezonereportui.admin` | View report history (`/reportadmin`) |

## Commands

| Command | Description |
|---------|-------------|
| `/report` | Open the in-game report form |
| `/reportadmin [n]` | Show the last *n* reports (default 15, requires `gamezonereportui.admin` or admin) |

## Localization

All player-facing strings are in the Oxide lang system. Edit `oxide/lang/en/GameZoneReportUI.json` to customize text or add additional languages.

## License

MIT — see [LICENSE](LICENSE)
