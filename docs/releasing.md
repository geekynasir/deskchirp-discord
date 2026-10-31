# Publishing a release

1. Complete the appropriate [acceptance checks](acceptance.md). Keep a release marked as a preview if hands-on checks remain pending.
2. Update `VERSION`, the numeric version in the Mac source plist and Windows app manifest, and the release notes at `docs/releases/v<VERSION>.md`. .NET reads `VERSION` automatically; Mac packaging applies its numeric portion to the app bundle.
3. Merge the reviewed source into `main` after CI passes. Create a tag whose name is `v` followed by the exact contents of `VERSION`, and push that tag.
4. The **Native apps** workflow runs the tests and builds on Mac, Windows and Linux. Its tag-only publishing job packages the Windows installer, optional portable Windows ZIP, two Mac app downloads, artwork pack and private linking service, verifies archive checksums/architectures, and publishes a GitHub Release with `SHA256SUMS`. Versions with a suffix such as `-preview.1` become prereleases.
5. Check the public release page, download names, checksums, and included `BUILD-INFO.json`. Confirm that the release tag points at the intended commit and that the docs describe the actual validation limits.

Only the publishing job has permission to write releases. Pull requests and ordinary builds have read-only repository access. Never commit signing credentials or device sign-in. Source archives are supplied by GitHub; native assets come from the successful build jobs for the same tag.

To rehearse packaging, download all four artifacts from one completed workflow into `artifacts/`, then run:

```sh
python3 scripts/package-release.py --artifacts artifacts --output release-assets \
  --version "$(cat VERSION)" --commit "$(git rev-parse HEAD)" \
  --run-url https://github.com/geekynasir/deskchirp-discord/actions
```

This does not publish anything. Mac executable permissions are preserved from the native ZIP; Windows downloads include the self-contained runtime. Windows setup is built with Inno Setup 6 and checked on the native runner, including Start launch, an older-version installer fixture, upgrade and uninstall. Its source/version metadata must match the release, and its checksum is published beside the ZIP checksums. Release signing, Mac notarization and automatic updates remain future work.
