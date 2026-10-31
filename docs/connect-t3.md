# Show your T3 activity on Discord

Deskchirp can show which agent tools are working in T3 Code, along with the number of tasks and helpers. Helpers are extra agents working on your tasks.

## Connect T3 Code

1. Open Discord desktop and T3 Code on your computer.
2. Turn on **Share on Discord** in Deskchirp. It finds Discord automatically; no Developer Portal setup is needed.
3. Choose **Allow T3 activity**. Deskchirp finds the local T3 address and opens your browser.
4. Approve **Read only** access. If T3 asks for a pairing code, create one in its **Settings → Sharing & access**, then enter it on T3’s page.
5. Return to T3 Code. Task and helper sharing turns on after approval.

The saved connection is restored on later launches. T3 currently grants access for 30 days; Deskchirp asks you to approve again when it expires. It never opens approval repeatedly in the background.

### Remote or custom installations

Local discovery reads only T3’s public `server-runtime.json` address and checks its read-only connection metadata. It also checks the default local port, 3773. It never opens T3’s encrypted connection catalog or reads saved tokens. If more than one environment answers, Deskchirp asks you to choose instead of guessing.

To select a remote environment, open **Advanced** in Deskchirp. In T3 Code, use **Settings → Environments → Copy MCP URL**, paste it into **T3 address**, and choose **Allow T3 activity**. Use HTTPS for a remote environment. Leave the address empty to discover the local app again. If T3 changes its local port after an update or restart, clear the old address and reconnect.

Deskchirp follows the approved environment, which can differ from the environment selected in T3’s desktop window. It does not merge every saved environment. Custom data folders are found when Deskchirp inherits `T3CODE_HOME`; otherwise use the address override.

## What friends see

For example, if Claude Code is doing the most work:

```
T3 Code · Claude Code
2 tasks · 4 helpers
```

The tool doing the most work is picked from current activity. Brief changes do not make the label jump back and forth. Helpers are counted under the tool actually running them, even when their parent task uses a different tool. Finished work does not add to the live count.

Some tools do not report every internal helper separately. The app can show only the work T3 makes available. It does not guess missing counts, and it does not claim that waiting work needs your approval.

If T3 cannot be reached or the activity is out of date, Discord falls back to **Using T3 Code**. Claude Desktop and Codex Desktop continue to show their app names; T3 activity is never added to those cards.

## Add logos

App and agent artwork is bundled and on by default. The card uses fixed public image URLs from the versioned [artwork pack](../assets/discord/README.md), which Discord fetches through its media proxy. Users do not need to register or upload anything. **Advanced → Show app icons** can turn it off. Custom application IDs work with the same public images.

The application's publisher icon is separate from card artwork: it is managed in the Discord Developer Portal by the owner of Deskchirp's application.

## Privacy and disconnecting

Task counts and agent tool names are optional. Project names, conversation titles, prompts, code, model names, and file paths are not published. Account names and custom connection labels are not used as public tool names.

T3 grants read-only access to thread metadata. The app uses it to read tool names and activity; it does not open conversations or run agents. Sign-in credentials stay in your computer's secure storage.

Pause sharing, turn it off, or quit to clear Discord. **Disconnect** removes this app's saved T3 connection. You can also revoke its access in T3's **Settings → Sharing & access**. If access expires or is revoked, choose **Allow T3 activity** again.

## Minimized and background apps

T3 does not need focus. Deskchirp observes whether its desktop app is running using only its exact app identifier or program name. Fresh running, waiting or starting T3 work takes priority over another active developer app. Otherwise the active developer app wins; when no supported developer app has focus, an open T3 app still shows its app-name card. Closing T3 clears its counts. Sharing off, pause, screen lock, sleep and linked-computer selection continue to hide activity.
