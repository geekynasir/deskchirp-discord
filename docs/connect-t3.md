# Show your T3 activity on Discord

Deskchirp can show which agent tools are working in T3 Code, how many tasks and helpers are running, and optionally token usage. Helpers are extra agents working on your tasks.

## Connect T3 Code

1. Open Discord desktop and T3 Code on your computer.
2. Turn on **Share on Discord** in Deskchirp. It finds Discord automatically; no Developer Portal setup is needed.
3. Choose **Allow T3 activity**. Deskchirp finds the local T3 address and opens your browser.
4. Approve **Read only** access. If T3 asks for a pairing code, create one in its **Settings → Sharing & access**, then enter it on T3’s page.
5. Return to T3 Code. Task and helper sharing turns on after approval.

The saved connection is restored on later launches. T3 currently grants access for 30 days; Deskchirp asks you to approve again when it expires. It never opens approval repeatedly in the background.

## Tasks running on another computer

On Windows, once T3 activity is allowed, Deskchirp also uses the connections saved in your T3 Code desktop app. Every environment T3 Code connects to, such as a server or another PC, appears in **T3 environments** marked **From T3 Code** and connects read-only on its own, without a browser. When you add or remove a connection in T3 Code, Deskchirp follows within about a minute. If the card style is **Usage**, or you already allowed token usage elsewhere, the new environments get token usage too.

To do this, Deskchirp opens T3 Code’s encrypted connection catalog with your Windows account, the same way T3 Code does. For each environment it uses T3 Code’s saved sign-in once, only with that environment’s own address, to create a one-time read-only pairing code, then signs in with that code. The pairing appears in T3’s **Settings → Sharing & access** as **Deskchirp**. T3 Code’s own sign-in is never saved, logged or sent anywhere else.

Some connections can’t be followed yet: ones that only go through T3 Connect, SSH connections, and plain `http://` addresses on another computer. Deskchirp names them under the switch; add them by hand as below if you can reach them another way.

- Removing an environment marked **From T3 Code** keeps it out of the list until you turn following off and on again.
- Removing token access for one keeps token usage off for it.
- Turn off **Use T3 Code’s connections** to stop reading the catalog. Environments it already added stay until you remove them.

### Adding an environment by hand

1. In T3 Code, open the remote environment’s menu and choose **Copy MCP URL**.
2. In Deskchirp, open **T3 environments → Add environment** and paste it.
3. Choose **Allow** next to the new environment and approve access in the browser.

Deskchirp follows every environment in the list and combines their counts. One unreachable environment doesn’t hide the others. If an environment is connected but never shows work, Deskchirp says so, which usually means your tasks run somewhere else.

Local discovery reads only T3’s public `server-runtime.json` address and checks its read-only connection metadata, plus the default local port 3773. Remove an environment to delete its saved sign-in.

## Choose what the card shows

**Card shows** has three styles:

| Style | Example |
| --- | --- |
| App only | `Using T3 Code` |
| Workload | `T3 Code · Claude Code` / `2 tasks · 4 helpers` |
| Usage | `T3 Code · Claude Code` / `842K tokens today` |

Other options, all off unless noted:

- **Small badge:** status (working / waiting, the default), the busiest tool’s logo, or none.
- **Project alias:** a name you type for a project, such as `Orbit Notes`. Real project names are never sent.
- **Model:** for example `claude-opus-5-5`, from the busiest tool’s most recent running task.
- **Estimated API cost:** for usage today only, shown as `~$1.84 API est.`. This is what the tokens would cost at API prices, not what you paid.
- **Elapsed timer** (on): how long the current stretch of work has lasted. Switching between running and waiting doesn’t reset it.
- **Playful wording:** `Herding 3 agents`, `Crunched 1.2M tokens today`, `Context tank 62% full`. Only real numbers are used.
- **Buttons:** none, a **Get Deskchirp** link, or that link plus a public GitHub repository for the aliased project.
- **Hide projects:** excluded projects are removed before anything is counted.
- **Pin a project or task:** counts only that project, or measures that task’s tokens and context.
- **Which app wins:** keep T3 work visible in the background (the default), or always follow the app you’re using.

The preview lists every field that will be sent and warns when a line may be cut off on Discord.

## Token usage

T3’s read-only connection doesn’t include token counts, so token usage needs a second permission for each environment:

1. In Deskchirp, choose **Allow token usage** next to the environment.
2. On the computer running that environment, run the command Deskchirp shows:
   `t3 pair --scope orchestration:read --scope diagnostics:read --label Deskchirp`
3. Paste the code or pairing link it prints into Deskchirp within 5 minutes. Each code works once.

This permission lasts 30 days. It also allows reading diagnostics and task details. Deskchirp uses it only for token counts and never stores or sends conversation content. **Remove token access** deletes it.

Usage options:

- **Today:** tokens counted by that computer’s provider tools today, including use outside T3. Unavailable while any project is hidden, because these totals can’t skip projects.
- **This session:** tokens from tasks that started during the current stretch of work.
- **Pinned task:** tokens for one task you choose.
- **Context:** how full the pinned task’s context window is.
- **Count:** all tokens, input only, or output only; compact (`842K`) or exact (`842,112`).

Missing, partial, or out-of-date numbers are never shown as zero. The card falls back to task counts instead.

## Logos

App, agent and status artwork is bundled and on by default. The card uses fixed public image URLs from the versioned [artwork pack](../assets/discord/README.md), which Discord fetches through its media proxy. **Advanced → Show app icons** turns it off.

## Privacy and disconnecting

Everything beyond the app name is optional. Project names, conversation titles, prompts, code, file paths, environment names and addresses are never published. Account names and custom connection labels are not used as public tool names.

Sign-in credentials stay in your computer’s secure storage. Pause sharing, turn it off, or quit to clear Discord. You can also revoke access in T3’s **Settings → Sharing & access**.

## Minimized and background apps

T3 does not need focus. Deskchirp observes whether its desktop app is running using only its exact app identifier or program name. With the default setting, fresh T3 work takes priority over another active developer app. Closing T3 clears its counts. Sharing off, pause, screen lock, sleep and linked-computer selection continue to hide activity.
