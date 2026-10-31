# Privacy

Deskchirp runs on your computer. Sharing is off until you enable it. It does not include analytics or a hosted account service.

## Discord

The app connects to the Discord desktop app on the same computer. By default it shares only `Using T3 Code`, `Using Claude`, or `Using Codex`. Optional T3 activity adds a public agent-tool name and task/helper counts. App and agent icons use fixed public artwork URLs fetched by Discord; icons can be turned off. Discord handles the activity under its own privacy policy.

Conversations, prompts, code, projects, model names, thread titles, window titles, and file paths are excluded from the Discord activity. The app does not need a Discord password, account token, bot token, or client secret.

## T3 Code

The optional connection uses browser-approved read-only access. It requests capabilities and thread lists, not conversation-reading tools. The thread list can contain metadata such as titles and project names; Deskchirp does not put those fields into its activity. T3 sign-in is saved in Keychain on Mac or Windows Credential Manager on PC. Disconnect removes the local saved sign-in; revoke access in T3 Code as well if you no longer want it authorized.

## Linked computers

Follow me is optional and requires your own private linking service. Your computers send their chosen names, availability, coarse recent-input timing, and a group-specific fingerprint of the Discord account. The service does not receive your app names, activity text, agent names, counts, T3 address, or conversations. Input timing comes from the operating system's idle timer; Deskchirp does not record keystrokes, pointer positions, or screens.

Membership, device-token hashes, account fingerprints, and revocation flags remain in the service database until the host removes them. Recent-input observations are kept in memory, not as an event history. Device credentials stay in native secure storage. Follow [the hosting and retention guide](../coordination/README.md).

## Local settings and reports

Settings retain your sharing choices, public Discord application ID, app mappings, and optional service addresses. Credentials use native secure storage. Troubleshooting may show local error details; review and redact them before sharing. Screenshots and reports you post on GitHub are public unless you use private vulnerability reporting.

Unlinking turns sharing off on that computer and removes its service credential after revocation succeeds. Other computers keep their own settings. Pause sharing is local to the computer where you choose it.

## Automatic setup

Deskchirp includes its publisher’s public Discord application ID. It discovers Discord’s local connection without asking for a Discord password or token. Sharing still starts only when you enable it.

When you choose **Allow T3 activity** without entering an address, Deskchirp reads only T3’s small public runtime-address file and checks local read-only connection metadata. It does not read the encrypted environment catalog, T3 sign-in tokens, logs or conversations. T3 still asks you to approve read-only access in your browser. Remote environments and ambiguous local installations use an address override under Advanced.

## Background T3 activity and images

T3 may remain minimized. Deskchirp compares running application identifiers or program names with its supported-app list; it does not read titles, command lines or other app contents. Fresh unfinished T3 work can remain visible while another app has focus. Quit T3, pause sharing or turn it off to clear it; lock, sleep and linked-computer rules still apply.

App and provider images are included and enabled by default. Only fixed public image URLs are sent; Discord fetches those images through its media proxy. No local paths or private user information are put in image URLs. The icon setting can turn artwork off.
