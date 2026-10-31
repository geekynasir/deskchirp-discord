# Follow you between computers

Developer Presence can select the linked computer you most recently used. For example, when you move from your Windows PC to your MacBook, the Windows app stops sharing and the MacBook takes over. Another MacBook can join the same group.

This feature is in development. It needs a private linking service; we do not currently operate a public one. [Set up the service](../coordination/README.md) before following these steps. Earlier builds do not have computer linking.

## Link your computers

1. Open Developer Presence settings → **Computers → Follow me**.
2. Paste your **Linking address**, choose a **Computer name**, and enter a fresh **Linking code**. Your service provides the first code.
3. Choose **Link this computer**. Linking leaves your sharing choice unchanged.
4. Choose **Link another computer** to get a code for your next Mac or PC. Use the same linking address there. Codes expire after five minutes and work once.
5. Sign into the same Discord account on each computer and use the same Discord application ID. Enable **Share on Discord** on the computers you want to participate.

Each computer still needs its own Discord desktop app running. To show T3 task/helper counts, connect T3 Code and enable **Show T3 activity** on each computer where you want those details. Signing into T3 does not link computers.

## Which computer wins?

The app uses recent input reported by the operating system. It does not record what you type, your pointer location, or your screen. Keeping T3 open or running more agents does not make a computer win.

The settings show **Selected computer: this computer** or the name of another linked computer. Near ties retain the current selection. Switching waits for the previous computer's permission to expire; it can take roughly twenty seconds plus network delay. The preview shows what this computer is preparing to share, not proof of what another person sees on Discord.

If you use a browser or another unsupported app on the selected computer, the developer activity hides. It does not switch back to an unattended PC with T3 open. Activity also hides after five minutes without input. Remote desktop and input-sharing software can create input on more than one computer; near ties keep the existing selection.

Task and helper counts come only from the selected computer's connected T3 environment. The app never adds duplicate counts just because two computers can see the same work.

## When something stops working

Locking, sleeping, quitting, turning sharing off, or pausing stops that computer from sharing. **Pause sharing affects this computer only.** Another linked computer can take over if it has recent input and sharing enabled.

If the linking service cannot be reached, the app stops sharing and shows **Waiting for a connection**. It does not guess which computer should win. Discord can take time to reflect a cleared activity, especially after a crash or a connection loss; real-client handoff checks remain pending.

If you change Discord accounts, the app asks you to check that all linked computers use the same account. It does not silently move your group to a different account. Use one Discord desktop channel per computer while validating this feature; Stable/Canary and multiple OS-user sessions still need real-machine checks.

## Privacy and unlinking

The service receives your chosen computer names, availability, coarse recent-use timing, and a group-specific account fingerprint. It does not receive your app names, Discord activity text, T3 address, agent names, task counts, or conversations. Device sign-in stays in Keychain or Windows Credential Manager.

**Unlink this computer** turns sharing off here, removes its saved device sign-in, and revokes its service connection. If the service is unreachable, sharing stays off and you can retry unlinking later. Other linked computers keep their own settings. Your service host can remove a lost computer; a management screen for that is not included yet.

Independent installations cannot agree on precedence. After unlinking, leave sharing off unless you intend to use only that computer. Third-party presence extensions and older builds are outside this selection system.
