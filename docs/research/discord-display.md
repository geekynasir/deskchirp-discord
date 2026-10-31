# Discord presentation research

Researched 2026-10-31. This is a proposal for the independent companion; it does not describe features already shipped in the development binaries.

## Verified surface

Official sources:

- [Rich Presence best practices](https://docs.discord.com/developers/rich-presence/best-practices)
- [Activity structure, assets, and buttons](https://docs.discord.com/developers/events/gateway-events#activity-object)
- [SET_ACTIVITY RPC](https://docs.discord.com/developers/topics/rpc#setactivity)
- [Setting Rich Presence, assets, and application name](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)
- Source inspected in `discord/discord-api-docs` at `9d2128bb8301e7d9f239ff259f2ee41cf4d73c70`.

The profile can show an application name, two primary detail/state strings, large artwork, a small overlay image, hover text for both images, an elapsed timer, and up to two link buttons. Current schemas also describe `details_url`, `state_url`, `large_url`, `small_url`, and `status_display_type`. Keep first implementation to well-supported fields; verify richer fields against our local IPC clients rather than assuming all Social SDK behavior transfers unchanged.

The Social SDK explicitly documents overriding the displayed application name with `Activity::SetName`. The registered application name remains the safe default for our current raw IPC implementation until override behavior is verified. Use our own companion application ID, not an official T3/Claude/Codex application's ID. A universal card can retain the companion name and identify the selected app in its artwork and details.

Discord's guidance is to keep snippets short and avoid repetition. It recommends 1024x1024 artwork and supports up to 300 uploaded assets, with lowercase asset keys. Uploaded assets support PNG, JPEG, and WebP, without animation. Current docs allow external public image URLs (including animation), but a small curated asset set avoids reliance on arbitrary remote images. Never upload private project favicons or screenshots automatically.

Do not model token counts as fake party capacity or completed work as game scores. Put usage and agent counts in honest text fields. Custom status (type 4) is not among the activity types accepted by SET_ACTIVITY. Product text should not promise removing Discord's own activity chrome or replacing the entire profile UI.

## Recommended artwork

- Large image: active app's T3 Code, Claude, or Codex artwork, provided we have appropriate reusable assets. Keep companion identity clear.
- Small image: either provider mark or activity badge (working, attention, paused). There is only one small overlay slot, so offer a choice rather than promising several simultaneous badges.
- Hover text: app/version, model, or optional usage breakdown. The same privacy rules apply to hover text as to the visible lines. On devices without hover, essential information must remain visible.
- Default buttons: public project repository only after explicit selection, and the companion's own download/about page. Never put authenticated T3 links, pairing codes, local file paths, or session access credentials into public buttons. A button does not grant collaboration access by itself.

## Proposed card presets

All examples below use fictional sample counts, with a constant companion application name. New metadata adapters are prerequisites.

### App only

```
Deskchirp
Using T3 Code
```

Large image is T3's logo. Equivalent cards can identify Claude and Codex without knowing anything about their conversations.

### Workload

```
Deskchirp
Using T3 Code
3 running · 1 needs attention
00:24:18 elapsed
```

Use confirmed thread states. Track top-level sessions and helpers separately. The clock must have a labeled meaning (focus duration, companion activity session, or observed thread duration) and must not silently switch meanings. A foreground app alone does not establish running agents or task completion.

### Usage

```
Deskchirp
T3 Code · 3 running
842K tokens · API ~$1.84
00:24:18 elapsed
```

The intended token scope is selected explicitly, but only if supported by the adapter. T3's existing aggregate RPC can support provider/time-window totals; it cannot alone establish pinned-thread or foreground-app-session usage. These are cumulative processed tokens only if the adapter genuinely supplies cumulative counters. Context-window occupancy is a separate metric. Cost is an estimate of API-equivalent usage unless the adapter can establish an actual billed charge; a subscription user should never see this labeled as actual money spent.

Project/model can replace one line or move to optional hover text. Discord is not an analytics dashboard; do not concatenate every available metric into one long line or rotate cards every few seconds to show all of them.

## Data integrity and privacy requirements

1. Declare each adapter's capabilities and provenance separately: foreground identity, current model, state, context usage, cumulative usage, and cost are independent capabilities.
2. If a capability is absent or stale, omit the field. Missing data is not zero. Partial usage/cost coverage must not be labeled a complete total.
3. Normalize provider-specific input, output, cache, and reasoning counters without double counting cached input or reasoning output already included in a parent category.
4. Deduplicate cumulative snapshots and mirrored sessions using proven provider/session identities. Do not sum every token-update event or infer duplicates from project names.
5. Round display (`842K`, `1.2M`) without inventing counts. Let users select scope, metric category, precision, and milestone thresholds. A milestone notification is based on observed usage, not a configurable fictional score.
6. Update from source events or bounded metadata polling, coalesce activity updates conservatively, and preserve the existing immediate clear controls. A timer is rendered by Discord, not by sending an update every second.
7. Rich metrics remain opt-in. Private project exclusions apply before aggregation, and their data must not leak through totals, tooltips, or buttons. If an aggregate source cannot separate excluded projects, disable that aggregate rather than claiming it has been filtered.
8. Default to hiding project/model/cost. Offer aliases and a complete preview of every outgoing field. Exclude prompts, code, raw tool arguments, conversation titles, paths, and command output.

## Scope boundaries

Current native builds still send only the approved `Using …` details text. This research does not implement artwork, new telemetry, cost calculations, buttons, or templates. It does not change upstream T3 Code or grant maintainer scope approval.
