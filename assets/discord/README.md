# Discord artwork

These four 1024×1024 PNGs match the app's optional artwork setting. Add them to the **same Discord application** whose Application ID you entered in Developer Presence.

1. Open [Discord Developer Portal](https://discord.com/developers/applications) and select your application.
2. Open its Rich Presence art assets and upload the PNGs.
3. Keep these exact lowercase names:

| File / asset name | Used for |
| --- | --- |
| `t3_code` | Large T3 Code image |
| `harness_claude` | Claude Code badge |
| `harness_codex` | Codex badge, using the OpenAI mark |
| `harness_antigravity` | Antigravity badge |

After the assets are available, turn on **Show app icons** in Developer Presence. Leave it off if you haven't uploaded them. The app cannot upload artwork into your Discord application for you.

The large image identifies T3 Code; the small badge identifies the agent tool doing the most work. Discord has one small badge slot. Other tools use the T3 image without a tool badge.

Source artwork comes from the [T3 Code repository](https://github.com/pingdotgg/t3code), inspected at `da04ebc5087477a82deae9ce19c03539a627359f`: `assets/prod/logo.svg`, `apps/marketing/public/harnesses/claude-ai-icon.svg`, `apps/marketing/public/harnesses/openai_dark.svg`, and `apps/mobile/assets/antigravity.png`. Marks identify their respective products; Developer Presence is independent and unaffiliated. Original marks retain their owners' rights.
