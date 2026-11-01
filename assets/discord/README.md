# Included Discord artwork

Deskchirp includes these 1024×1024 PNGs in both apps, in the Windows installer and in the separate artwork download. Artwork is on by default. Fixed public URLs under the immutable `artwork-v2` Git tag (`artwork-v1` stays available for older builds) are used in activity payloads; Discord fetches them through its media proxy. A user never needs to upload assets or configure keys. The optional artwork toggle remains under Advanced.

| File | Purpose |
| --- | --- |
| `deskchirp.png` | Deskchirp logo; also provided as SVG, Windows ICO and Mac ICNS in `assets/brand` |
| `t3_code.png` | T3 Code card image |
| `harness_claude.png` | Claude app image / Claude Code badge |
| `harness_codex.png` | Codex app image / Codex badge, using the OpenAI mark |
| `harness_antigravity.png` | Antigravity badge |
| `harness_cursor.png` | Cursor badge |
| `harness_opencode.png` | OpenCode badge |
| `harness_grok.png` | Grok badge |
| `status_working.png` | Working / starting status badge |
| `status_attention.png` | Waiting status badge |
| `status_idle.png` | Idle status badge |

The large image identifies the app; Discord's single small badge identifies the busiest supported T3 tool. Muse, Pi and unknown tools have no provider badge until verified artwork is available. No model logos are inferred from model names.

The native Deskchirp app icon is included in the executable / app bundle. The Discord **application icon** is a separate publisher setting: upload `deskchirp.png` under General Information for application `1566210608569782373`. No user account tokens or passwords belong in Deskchirp. Publisher account access is needed for this one-time change; users of the app do not need to do it.

Discord documents [external image URLs](https://docs.discord.com/developers/events/gateway-events#activity-object-activity-asset-image). Live desktop rendering should still be checked on supported Discord clients.

Deskchirp's original logo and the status badges are MIT licensed with the project. Product artwork comes from [T3 Code](https://github.com/pingdotgg/t3code/tree/da04ebc5087477a82deae9ce19c03539a627359f) at commit `da04ebc5087477a82deae9ce19c03539a627359f`: `assets/prod/logo.svg`, `apps/marketing/public/harnesses/claude-ai-icon.svg`, `openai_dark.svg`, `cursor_light.svg`, `opencode-dark.svg`, `grok-dark.svg`, and `apps/mobile/assets/antigravity.png`. The upstream MIT notice is in `licenses/t3code-MIT.txt`. Marks identify observed products; Deskchirp is independent and unaffiliated. Trademark rights remain with their owners.
