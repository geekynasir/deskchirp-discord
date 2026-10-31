# Product language

The words Deskchirp uses in its macOS and Windows settings, menus, status messages, and Discord activity. Platform code should take its strings from this file. Technical terms stay in source, logs, and developer docs.

This guide doesn't change any approved behavior. Sharing stays off by default. T3 access is read-only. Only the active app is in scope. When T3 activity is unavailable, the app shows the app name only. Thread titles, models, projects, prompts, paths, private URLs, and credentials are never published.

Method: [Impeccable](https://github.com/pbakaus/impeccable) 4.8.0 `clarify`, Operate mode, and the craft floor. The project context launcher didn't run, and there is no PRODUCT.md or DESIGN.md, so this guide uses only the existing source and `spec/t3-activity.md`.

Follow-up review on 2026-10-31: the user confirmed **Unslop** as the intended name. Applied the audit guidance from [Unslop by Damir Sokolovsky (@heydamir)](https://github.com/heydamir/unslop/blob/main/skills/unslop/SKILL.md), licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). This review note adapts that guidance. The earlier “Stack Cursor” attribution remains unverified.

The review covered both native settings implementations, their public status messages, the task/helper formatters, the connection guide, and existing build screenshots: macOS light/dark and Windows light. No decorative gradients, emoji labels, hype copy, or unnecessary animation were found in those surfaces. The settings explain helpers, keep connection terminology out of normal status messages, and show a concrete next step when setup fails. Discord uses short tool names and task/helper counts, with waiting and starting work labeled explicitly. Retained the native controls and existing wording; this pass changed documentation only. The screenshots cover only the captured portion of settings, and do not establish accessibility, scaling, live sign-in, or real Discord rendering. Those checks remain in `docs/acceptance.md`.

## Voice

The linked-computer extension uses **Follow me**, **Linking address**, **Computer name**, **Linking code**, **Link this computer**, **Link another computer**, and **Unlink this computer** (with native button capitalization). Use **Selected computer: this computer** or **Selected computer: {name}**, not a claim that a remote Discord profile has already updated. **Switching computers…**, **Waiting for a connection**, and **No computer is sharing** describe the other states. Explain that switching waits for the previous computer, hides after five minutes without input, and that Pause affects this computer only. The Computers area discloses the computer names and recent-use timing sent to the private linking service. Device names never appear on the Discord card.

- Plain, calm, short. Lead with the state the user is in, then add the next step only if there is one.
- Errors say what failed and how to fix it. Never show an exception message, error code, socket path, or protocol name as the main text. Write those to the log.
- Don't promise what the app can't know. Don't say "needs attention" or "finished", don't show a zero count, and don't claim a cause the app didn't observe.
- Use typographic apostrophes (’) and the ellipsis character (…).
- Capitalization follows each platform:
  - **macOS:** menu items and buttons use title case ("Pause Sharing", "Restore Defaults"). Toggle labels, section headers, and help text use sentence case.
  - **Windows:** sentence case everywhere ("Pause sharing", "Restore defaults").
- Build whole messages from format strings that have named counts. Never build a plural by adding "s". That keeps the strings translatable.

## Glossary

| Use in product copy | Meaning (for implementers) | Never show users |
|---|---|---|
| **Share on Discord** | Master sharing switch | presence broadcast, RPC |
| **Pause sharing** | Temporarily stop sharing without turning it off | — |
| **Active app** | The frontmost, focused app | foreground, focused process, frontmost |
| **Discord application ID** | The public numeric ID from the Developer Portal | client ID, snowflake, App ID (alone) |
| **Discord preview** | The exact text Discord will show | outgoing preview, payload, activity object, SET_ACTIVITY |
| **Connected to Discord** | Local link to the Discord desktop app | IPC, socket, named pipe, handshake, READY, frame |
| **Locked or asleep** | The session is locked, asleep, or disconnected | session suspended, inactive session, PBT_*, WTS_* |
| **Last updated** | When Discord confirmed the last change | acknowledged, ACK, nonce |
| **T3 address** | The URL from T3 Code's **Copy MCP URL** | MCP endpoint, resource, origin |
| **Sign in / approve access** | T3's own browser approval flow | OAuth, PKCE, token, scope, `orchestration:read`, client registration |
| **Agent tool** | The tool or harness running the work: Claude Code, Codex, Antigravity, Cursor, OpenCode, Grok, Muse, Pi | harness, driver, driverKind, provider instance |
| **Task** | A top-level T3 thread or fork | thread (in Discord), session |
| **Helper** | An extra agent that a task started (`relationshipToParent = subagent`) | subagent, child thread, sub-agent |
| **Other tools** | A driver that isn't on the known list. Its string is never repeated. | the raw driver name |
| **App identifier** (macOS) | Bundle identifier | bundle mapping |
| **Program file** (Windows) | Executable name | executable mapping, process name |

**What "helpers" means.** The word is ambiguous on its own, so wherever the app first mentions helpers, it adds this sentence: **“A helper is an extra agent that a task starts to handle part of its work.”** On Discord, a helper count always appears next to a task count or under an agent tool's name, and it never appears without a number. In settings, the word "task" gets this one-time bridge to T3's own vocabulary: “Each T3 thread counts as a task.”

## Discord activity

### Shape

| Situation | `details` (line 1) | `state` (line 2) |
|---|---|---|
| Sharing on, T3 Code active, T3 activity off/unconnected/unavailable/stale/no work | `Using T3 Code` | *(omitted)* |
| T3 activity on, a winning agent tool has work | `T3 Code · {tool}` | count line (below) |
| Claude Desktop active | `Using Claude` | *(omitted; T3 data never appears here)* |
| Codex Desktop active | `Using Codex` | *(omitted; T3 data never appears here)* |
| Anything else (off, paused, locked, unsupported app, invalid ID) | nothing is sent and the activity is cleared | — |

`{tool}` is one of: `Claude Code`, `Codex`, `Antigravity`, `Cursor`, `OpenCode`, `Grok`, `Muse`, `Pi`, `Other tools`. The separator is a middle dot with a space on each side (" · ", U+00B7).

### Count line

Each count uses a singular and a plural form. A part with a count of zero is left out, and the app never shows a zero.

| Count | Task | Helper |
|---|---|---|
| 1 | `1 task` | `1 helper` |
| n > 1 | `{n} tasks` | `{n} helpers` |

Pick the first rule that has a nonzero count:

1. **Running** (only the `running` status): tasks, then helpers, joined with " · ". No verb is added, so running is implied.
   `2 tasks · 4 helpers` · `1 task · 1 helper` · `3 tasks` · `2 helpers`
2. **Waiting** (nothing running): every part gets its own verb, so the line can't be misread as a mix of running and waiting.
   `2 tasks waiting` · `1 helper waiting` · `1 task waiting · 2 helpers waiting`
3. **Starting** (includes queued; nothing running or waiting): `1 task starting` · `2 tasks starting · 1 helper starting`
4. Otherwise (only finished, cancelled, or failed rows): no state line. Fall back to `Using T3 Code`.

Waiting and starting counts are never added to running counts, and a running line never carries a waiting suffix. All lines stay well under Discord's 128-character field limit. The longest possible line is the waiting combination with three-digit counts, at about 40 characters.

Examples:

```
T3 Code · Claude Code          T3 Code · Antigravity          Using T3 Code
2 tasks · 4 helpers            1 task waiting
```

### App icons (optional, off by default)

| Field | Value |
|---|---|
| `large_image` | `t3_code` |
| `small_image` | `harness_claude` (Claude Code), `harness_codex` (Codex), `harness_antigravity` (Antigravity). Leave it out for every other tool and for Other tools. |
| `large_text` / `small_text` | **Root decides.** If used, set them only to `T3 Code` and the same public tool name already in `details`. Never use any other text. |

Icons are sent only when **Show app icons** is on. Asset keys belong in the setup guide, never in the settings UI.

## Settings: layout and hierarchy

Keep the native controls and system appearance: SwiftUI `Form(.grouped)` on macOS, WinUI with Mica on Windows. This is a reorder and relabel, not a redesign. The order follows what the user needs to know: am I sharing → what will Discord show → set up Discord → optional T3 detail → advanced.

**macOS · General tab** (`SettingsView.swift`)

1. Section without a header: **Share on Discord** and **Pause sharing**. Disable Pause while sharing is off; the menu already does this, but Settings doesn't.
2. Section **Discord preview**: the details line (`.headline`), the state line if there is one (`.body`), one status line (`.secondary`), an optional recovery line, and the caption **Last updated {time}**.
3. Section **T3 Code**: the **Show T3 activity** toggle and its help, the **T3 address** field and its help, `LabeledContent("Status")`, and **Connect T3 Code** or **Disconnect**.
4. Section **Discord**: the **Discord application ID** field, its help and validation, the **Open Discord Developer Portal** link, the **Show app icons** toggle, its help, and the **How to Add the Icons** link.
5. Footer for the last section: the privacy sentence (see below).
6. Rename the second tab to **App Detection**. Move the active-app identifier there; it's for troubleshooting.

**Windows** (`MainWindow.xaml`): the same order inside the existing `StackPanel`. Keep the preview `Border` card. Give the T3 Code group a `BodyStrongTextBlockStyle` header and wrapping help text with `TextFillColorSecondaryBrush`. Rename the expander to **App detection**. Add a second expander, **Troubleshooting**, for the Active app and Discord connection rows.

The macOS settings window sits at 520 pt minimum width, so help text must wrap: use `.fixedSize(horizontal: false, vertical: true)` or `TextWrapping="Wrap"`. Don't truncate. At 200% zoom on Windows, labels wrap instead of clipping.

## Settings copy

### Sharing

| Element | Copy |
|---|---|
| Windows header subtitle (`MainWindow.xaml:15`) | Show your Discord friends which developer app you’re using. |
| Master toggle (`SettingsView.swift:17`, `MainWindow.xaml:18`) | **Share on Discord** |
| Pause toggle | **Pause sharing** |

### Discord preview

| Element | Copy |
|---|---|
| Section title (`SettingsView.swift:41`, `MainWindow.xaml:32`) | **Discord preview** |
| Nothing being sent (replaces `None / Cleared`) | **Not shown on Discord** |
| Updated time (replaces `Last acknowledged`, `Last acknowledged update`) | **Last updated {time}**. Use the locale's time format: SwiftUI `.formatted(date: .omitted, time: .shortened)`; Windows `ToString("t")` instead of the hard-coded `"HH:mm:ss"` at `MainViewModel.cs:252`. Hide the row until the first update instead of showing `Never`. |
| Activity note (one line under the preview) | Not opted in: *(none)* · Not connected: **Connect T3 Code to show agent activity.** · No running, waiting, or starting work: **No tasks right now, so only the app name is shown.** · Unavailable, stale, or incomplete: **T3 activity isn’t available right now, so only the app name is shown.** |
| Accessibility label | “Discord will show: {details}. {state}.” Replace " · " with ", " so screen readers don't read out "middle dot". |

### T3 Code

| Element | Copy |
|---|---|
| Section title | **T3 Code** |
| Activity toggle | **Show T3 activity** |
| Toggle help | While T3 Code is the active app, also show which agent tool is busiest and how many tasks and helpers it’s running. A helper is an extra agent that a task starts to handle part of its work. Each T3 thread counts as a task. |
| Address field label | **T3 address** |
| Address placeholder | Paste from T3 Code |
| Address help | In T3 Code, open Settings → Environments and choose Copy MCP URL. |
| Connect button | macOS **Connect T3 Code** / Windows **Connect T3 Code** |
| Consent line under Connect | Opens your browser so you can approve read-only access in T3 Code. Deskchirp only reads which agent tools are working and how many tasks and helpers they have. |
| Disconnect button | **Disconnect**. Accessible name: “Disconnect T3 Code”. No confirmation, because reconnecting is easy. Help after disconnecting: “To revoke access in T3 too, open T3 Code Settings → Sharing & access.” |
| Stored credential note (shown once connected) | macOS: Your sign-in is saved in Keychain. · Windows: Your sign-in is saved in Windows Credential Manager. |

### Discord

| Element | Copy |
|---|---|
| Section title | **Discord** |
| ID field label (`SettingsView.swift:31`, `MainWindow.xaml:22`) | **Discord application ID**. The label is the same on both platforms; macOS currently says only “Application ID”. |
| Windows placeholder (`MainWindow.xaml:24`) | 15–20 digits |
| ID help | Find it under General Information for your app in the Discord Developer Portal. It’s public, not a password or token. |
| ID validation (`SettingsView.swift:36`) | Application IDs are 15 to 20 digits. Check that you copied the whole number. |
| Portal link | macOS **Open Discord Developer Portal** · Windows **Open the Discord Developer Portal** |
| Icons toggle | **Show app icons** |
| Icons help | Adds the T3 Code icon and agent tool icons to your status. Add the icons to your Discord application first, or they won’t appear. |
| Icons link | macOS **How to Add the Icons** · Windows **How to add the icons**. This points to the Add logos section of `docs/connect-t3.md`, which links `assets/discord/README.md`. |

### Privacy footer

This replaces `SettingsView.swift:51` and `MainWindow.xaml:79`. The current text says task counts aren't shared, which becomes false once T3 activity is on.

| Element | Copy |
|---|---|
| T3 activity off | Discord sees only the name of the app you’re using. Conversations, window titles, files, and projects are never shared. |
| T3 activity on | Discord sees the app name and, while T3 Code is active, an agent tool name with task and helper counts. Conversations, thread titles, projects, models, and files are never shared. |
| Windows only, second sentence | Close this window to keep running in the notification area. |

### App detection (advanced)

| Element | Copy |
|---|---|
| Tab or expander | macOS tab **App Detection** · Windows expander **App detection** |
| Intro (`SettingsView.swift:59`, `MainWindow.xaml:46`) | If T3 Code, Claude, or Codex isn’t recognized, add its version here. |
| macOS field (`SettingsView.swift:81,83`) | **App identifier**. Help: “Also called a bundle ID, for example com.company.app.” |
| Windows field (`MainWindow.xaml:48`) | **Program file**. Placeholder: `example.exe`. Help: “The .exe name shown in Task Manager under Details.” |
| Picker / ComboBox (`SettingsView.swift:85`, `MainWindow.xaml:51`) | **Recognize as** |
| Add (`SettingsView.swift:90`, `MainWindow.xaml:57`) | macOS **Add** · Windows **Add app** |
| Remove, accessible names (`SettingsView.swift:77`, `MainWindow.xaml:71`) | “Remove {identifier}” / “Remove {file}.exe”. Bind the identifier; don't use the generic “application mapping”. |
| Restore (`SettingsView.swift:96`) | macOS **Restore Defaults** |
| Unknown row (`SettingsView.swift:67`, `MainViewModel.cs:33`) | Unknown app |
| Active-app identifier (`SettingsView.swift:61`) | **Last active app: {identifier}**. See the open question below about Deskchirp detecting itself. |
| Bad file name (`MainViewModel.cs`, `AddProcessMapping`) | Enter only the file name, like example.exe, without a folder path. |

## Status and recovery messages

A status is one line with the state, plus at most one line with the next step. It replaces both `statusMessage` and Windows' raw `ConnectionStatusText` (`ConnectionStatus.ToString()` at `MainViewModel.cs:243`); drop the separate enum line. Use "Mac" in macOS strings and "PC" in Windows strings.

### Sharing gate

| Current string(s) | Status line | Recovery line |
|---|---|---|
| `Disabled`, `Sharing disabled` | Sharing is off | — |
| `Paused`, `Presence paused` | Sharing is paused | — |
| `Session Locked / Inactive`, `Session locked (presence cleared)`, `System suspended (presence cleared)`, `Session locked`, `System suspended`, `None / Cleared (Locked or Sleeping)` | Hidden while your Mac/PC is locked or asleep | — |
| `Missing Application ID` | Add your Discord application ID to start sharing | — |
| `Invalid Application ID`, `Invalid Discord Application ID`, `Missing or invalid Discord Application ID.` | Check your Discord application ID | Application IDs are 15 to 20 digits. |
| `Idle (Supported app not focused)`, `No supported developer app in foreground` | Not sharing while another app is active | Shares when T3 Code, Claude, or Codex is the active app. |
| `Preparing activity for {app}`, `Waiting to update: {headline}` | Updating Discord… | — |
| `Updated: {headline}`, `Connected to Discord`, `Connected to Discord IPC.` | Sharing on Discord | — *(caption: Last updated {time})* |
| `Ready` (initial) | Use the gate message that applies; never show “Ready” | — |

### Discord connection

| Current string(s) | Status line | Recovery line |
|---|---|---|
| `Connecting to Discord at {path}...`, `Scanning Discord named pipes (0..9)...`, `Connecting to Discord (retrying)...` | Connecting to Discord… | — |
| `No Discord IPC candidate sockets found.`, `Discord desktop client not found on local named pipes.` | Couldn’t find Discord | Open the Discord desktop app. Discord in a browser can’t show this status. |
| `Connection and handshake timed out.`, `Handshake timed out waiting for READY.`, `Handshake failed: …`, `Operation timed out.` | Discord didn’t respond | Make sure Discord is open and signed in. Deskchirp will keep trying. |
| `Discord rejected the connection.`, `Discord error: {msg}`, `Discord SET_ACTIVITY error: {msg}`, `Failed to send SET_ACTIVITY: …` | Discord didn’t accept the update | Check that your Discord application ID matches an app in the Developer Portal. |
| `Discord closed IPC connection.`, `Discord IPC disconnected.`, `Disconnected from Discord.`, `Discord closed connection.`, `Discord disconnected: …`, `Cleared and disconnected` | Discord disconnected | Reconnects when Discord is running again. |
| `Discord framing error: …`, `Protocol framing error: …`, `Invalid JSON received from Discord IPC: …`, `Connection error: …`, `Connection attempt failed: …`, socket/transport errors | Lost the connection to Discord | Reconnecting… |
| `Connection canceled.` | *(no user message; use the gate message)* | — |
| `Couldn't save settings: {ex.Message}` | Couldn’t save your settings | Try again. If it keeps happening, quit and reopen Deskchirp. |
| `Couldn't start sharing: {ex.Message}` | Couldn’t start sharing | Quit and reopen Deskchirp. |

"Will keep trying" and "Reconnects" depend on the bounded automatic reconnect in `spec/behavior.md`. If a platform doesn't retry in that state, use “Open Discord, then turn sharing off and on again.” instead.

### Windows connection-state labels

Only for Troubleshooting, if root keeps the row: Not connected · Connecting… · Connected · Couldn’t connect.

### T3 connection status

These labels come from the spec, with recovery added.

| Status label | When | Recovery / detail line | Action |
|---|---|---|---|
| **Not connected** | No saved sign-in | Paste the T3 address, then connect. | Connect T3 Code |
| **Opening sign-in…** | Browser launched, callback pending | Approve Read only access in your browser, then come back here. If T3 asks for a pairing code, create one in T3 Code under Settings → Sharing & access. | Cancel *(accessible name: “Cancel T3 sign-in”)* |
| **Connected** | Valid credential | Activity is checked only while T3 Code is the active app. | Disconnect |
| **Sign in again** | 401 or expired token | Your T3 Code sign-in expired. Connect again to keep showing activity. | Connect T3 Code |
| **Couldn’t reach T3 Code** | Network failure or timeout | Check that T3 Code is running and the address is right. | Connect T3 Code |

### T3 address and sign-in errors

These appear inline under the T3 address field or as the status detail line.

| Condition | Message |
|---|---|
| Empty on Connect | Paste the address from T3 Code first. |
| `http://` to a host other than localhost | Use an address that starts with https://. Plain http:// works only for T3 Code on this computer. |
| Has userinfo, query, or fragment; wrong path; not a URL | This doesn’t look like a T3 Code address. Copy it again from Settings → Environments → Copy MCP URL. |
| Discovery/metadata invalid or on another origin | This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL. |
| Redirect refused | T3 Code sent Deskchirp to a different address. Copy the address again from T3 Code. |
| Browser couldn't open | Couldn’t open your browser. Check that you have a default browser, then try again. |
| User denied or cancelled in browser | Sign-in was cancelled. *(If a previous connection is still saved, add:)* Your existing connection wasn’t changed. |
| State/issuer mismatch, bad callback | Sign-in didn’t finish. Try connecting again. |
| Timeout waiting for callback | Sign-in timed out. Try connecting again. |
| Keychain / Credential Manager write failed | Couldn’t save your sign-in to Keychain. Try connecting again. · Windows: …to Windows Credential Manager… |
| Snapshot incomplete, malformed, or capped | *(Status stays **Connected**; the preview note reads:)* T3 activity isn’t available right now, so only the app name is shown. |

### Browser callback page

The local callback page is the HTML shown in the user's browser after T3 approval. Keep it plain text with no branding claims, and include no address or token.

- Success title: **Deskchirp is connected to T3 Code**. Body: “You can close this tab and return to Deskchirp.”
- Failure title: **Sign-in didn’t finish**. Body: “Return to Deskchirp and try connecting again.”

## Menu bar (macOS) and notification area (Windows)

**macOS menu** (`MenuBarContentView.swift`)

| Item | Copy |
|---|---|
| Header (`:15`) | Deskchirp |
| Status (`:18`) | the status line alone, without a “Status:” prefix |
| Preview (`:28`) | the details line, then the state line as a second item if there is one. No “Preview:” prefix. Show nothing extra when not shown on Discord. |
| Active app (`:21–25`) | Remove from the menu. The status line already explains why nothing is shared. Don't show the unrecognized identifier here. |
| Sharing (`:33`) | `Toggle("Share on Discord")` with a native checkmark. This replaces the “Enable/Disable Presence Sharing” label flip. |
| Pause (`:37`) | **Pause Sharing** / **Resume Sharing** (unchanged), disabled when sharing is off |
| Settings, Quit | unchanged |

**Windows tray** (`TrayIcon.cs:114–120`, `MainWindow.xaml.cs:150`)

| Item | Copy |
|---|---|
| Open | **Open Deskchirp** (was “Show Deskchirp”) |
| Pause | **Pause sharing** / **Resume sharing** (was “Pause/Resume Presence”). Drop `MF_CHECKED`, because a checked “Resume” item contradicts itself. |
| Quit | **Exit** (the Windows convention) |
| Tooltip | `Deskchirp` + newline + the details line, plus “ · {state}” if there is one; or + newline + `Not shown on Discord`. Keep within `szTip`'s 128 characters. |

## Kept out of ordinary UI

| Term or data | Where it may appear |
|---|---|
| foreground, IPC, socket, named pipe, handshake, frame, READY, SET_ACTIVITY, payload, nonce, ACK | Source, logs, developer docs |
| snowflake, client ID | Source only. The UI always says Discord application ID. |
| bundle identifier, executable mapping | App Detection / App detection only, using the plain labels above |
| session suspended / inactive session | Source only. The UI says locked or asleep. |
| MCP | Only inside the quoted T3 menu item "Copy MCP URL" |
| OAuth, PKCE, scope, token, `orchestration:read` | Source, logs, developer docs |
| Asset keys (`t3_code`, `harness_*`) | Setup guide only |
| Raw exception text, Discord error messages, URLs, paths | Log only, never status text |
| Display names of provider instances or accounts, unknown driver strings, models, thread titles, project names | Nowhere: not in UI, not in Discord |

## Open questions for root

1. **Self-detection on macOS.** While Settings is open, the frontmost app is Deskchirp itself, so “Current app” shows Deskchirp's own identifier. Have the detector keep the last identifier that isn't its own, for local display only, and show it as **Last active app**. Optionally add an **Add This App** button next to it.
2. **Winner with no running work.** The copy assumes the chosen tool shows the highest-priority non-empty bucket: running, then waiting, then starting. If root publishes starting-only states as `Using T3 Code` to avoid churn, rule 3 is never used.
3. **Disconnect.** This matches `docs/connect-t3.md`: Disconnect removes this app's saved T3 connection from this computer, and the stored credential must actually be deleted. Server-side revocation stays in T3 (Settings → Sharing & access); the app must not claim it revoked anything.
4. **Icons on app-only T3 presence.** Decide whether `large_image = t3_code` is also sent when T3 activity is off. The icon help text holds either way.
5. **Hover text.** Decide whether to use `large_text` / `small_text`; see App icons.

## Strings asserted in tests and docs

Root decisions for this implementation: keep foreground detection behavior unchanged; show waiting then starting when nothing is running; disconnect deletes the local sign-in without claiming server revocation; icons remain an independent opt-in for T3; hover text contains only T3 Code and the same public tool name already shown. The shared synthetic counting cases live in `spec/t3-activity-cases.json`.

Tests that assert `None / Cleared`, `Disabled`, and `Paused`:

- `macos/Tests/.../PresenceCoordinatorTests.swift:101,102,165,166`
- `ActivityFormatterTests.swift:99`
- `windows/tests/.../PresenceCoordinatorTests.cs:129,289`
- `ActivityFormatterTests.cs:96`
- `SharedPresenceFixturesTests.cs:114`

These are app-only UI states. Changing them doesn't change the Discord payload fixtures.

User docs that say task counts aren't shared and need the conditional wording above once T3 activity ships:

- `README.md:16`
- `macos/README.md:23`
- `docs/trying-builds.md:26`
