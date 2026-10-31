# Show your T3 activity on Discord

Developer Presence can show which agent tools are working in T3 Code, along with the number of tasks and helpers. Helpers are extra agents working on your tasks.

## Connect T3 Code

1. In T3 Code, open **Settings → Environments**, open your environment's menu, and choose **Copy MCP URL**.
2. Open Developer Presence settings and paste it into **T3 address**.
3. Choose **Connect T3 Code**. Your browser opens T3's sign-in page.
4. Approve **Read only** access. If T3 asks for a pairing code, create one in its **Settings → Sharing & access**. Enter that code on T3's page.
5. Turn on **Show T3 activity** and Discord sharing, then return to T3 Code.

The app follows the connected T3 environment. It does not automatically connect every environment saved in T3. The connection needs HTTPS, or a localhost address when T3 runs on the same computer.

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

Artwork is optional. Upload the included [Discord artwork](../assets/discord/README.md) to your Discord application, then enable **Show app icons**. You can use the activity feature without artwork.

## Privacy and disconnecting

Task counts and agent tool names are optional. Project names, conversation titles, prompts, code, model names, and file paths are not published. Account names and custom connection labels are not used as public tool names.

T3 grants read-only access to thread metadata. The app uses it to read tool names and activity; it does not open conversations or run agents. Sign-in credentials stay in your computer's secure storage.

Pause sharing, turn it off, or quit to clear Discord. **Disconnect** removes this app's saved T3 connection. You can also revoke its access in T3's **Settings → Sharing & access**. If access expires or is revoked, choose **Connect T3 Code** again.
