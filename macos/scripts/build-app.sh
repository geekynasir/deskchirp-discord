#!/usr/bin/env bash
set -euo pipefail

# Build script for Developer Presence macOS application bundle.
# Usage: ./scripts/build-app.sh [--release|--debug]

CONFIGURATION="release"
if [[ "${1:-}" == "--debug" ]]; then
    CONFIGURATION="debug"
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

echo "==> Building DeveloperPresence (${CONFIGURATION})..."
cd "${PACKAGE_DIR}"
swift build -c "${CONFIGURATION}"

BIN_PATH="$(swift build -c "${CONFIGURATION}" --show-bin-path)"
EXECUTABLE="${BIN_PATH}/DeveloperPresence"

if [[ ! -f "${EXECUTABLE}" ]]; then
    echo "Error: Built executable not found at ${EXECUTABLE}" >&2
    exit 1
fi

APP_BUNDLE="${PACKAGE_DIR}/dist/DeveloperPresence.app"
CONTENTS_DIR="${APP_BUNDLE}/Contents"
MACOS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

echo "==> Packaging ${APP_BUNDLE}..."
rm -rf "${APP_BUNDLE}"
mkdir -p "${MACOS_DIR}" "${RESOURCES_DIR}"

# Copy binary
cp "${EXECUTABLE}" "${MACOS_DIR}/DeveloperPresence"
chmod +x "${MACOS_DIR}/DeveloperPresence"

# Copy Info.plist
cp "${PACKAGE_DIR}/Sources/DeveloperPresence/Info.plist" "${CONTENTS_DIR}/Info.plist"

# Create PkgInfo
echo -n "APPL????" > "${CONTENTS_DIR}/PkgInfo"

# Ad-hoc code signing if codesign utility exists (macOS)
if command -v codesign &>/dev/null; then
    echo "==> Applying ad-hoc code signature..."
    codesign --force --deep --sign - "${APP_BUNDLE}"
fi

echo "==> Build successful: ${APP_BUNDLE}"
