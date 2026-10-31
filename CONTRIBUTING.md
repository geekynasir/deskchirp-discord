# Contributing to Deskchirp

Thanks for helping make it easier to share what you're working with. Open an issue before starting a new integration or a large change so we can agree on its scope. Small fixes can go straight to a pull request.

## Build and test

- macOS 13 or newer with Swift 5.9 or newer: `swift test --package-path macos`, then `bash macos/scripts/build-app.sh`. See [Mac development](macos/README.md).
- Windows with .NET 8 and the Windows App SDK prerequisites: follow [Windows development](windows/README.md). The portable core tests also run on Linux and macOS with `dotnet test windows/tests/DeveloperPresence.Tests/DeveloperPresence.Tests.csproj`.
- Settings presentation: `dotnet test checks/WindowsShell.Tests/WindowsShell.Tests.csproj`.
- Linking service, Python 3.10 or newer on Unix: `python3 -m unittest discover -s coordination/tests -v`. With .NET 8 installed, `python3 coordination/tests/native_http.py` checks two client services against the real HTTP server.

Keep native controls and platform code in their own directories. Shared behavior belongs in `spec/`; update the fixtures and both implementations when behavior changes. Test fixtures must use fictional data. Add regression tests for meaningful behavior changes, and explain any checks that need real hardware in the pull request.

## Privacy and product language

Sharing starts off. Never add conversation reading, transcript access, window-title collection, project paths, or private provider labels to the Discord activity. Unsupported or unavailable data must stay absent rather than becoming a guessed count. Computer linking must not fall back to independent publishing when the service is unavailable.

Use familiar language in settings and status messages. Explain helpers, recovery steps, and the consequences of a setting where they appear. See [product language](docs/product-language.md), [behavior](spec/behavior.md), and [acceptance checks](docs/acceptance.md).

Do not include credentials, linking codes, account IDs, conversations, or private addresses in issues, screenshots, logs, or pull requests. Report security issues privately using [the security policy](SECURITY.md).

## Pull requests

Describe the problem, the resulting behavior, and how you checked it. Include screenshots for visible changes and identify the OS and architecture used. Keep unrelated cleanup separate. Be kind, specific, and respectful when giving feedback.

Contributions are provided under this repository's MIT license. Preserve existing third-party license notices; adding a dependency or artwork requires documenting its source and license in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Publishing

The maintainer follows [the release guide](docs/releasing.md). A tag must match `VERSION`; the release workflow runs all checks, builds the native apps, packages the downloads, and publishes their checksums. Preview releases remain marked as prereleases until real-device acceptance is complete.
