#!/usr/bin/env python3
"""Package checked native build artifacts for a GitHub release, without installing them."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import plistlib
import re
import shutil
import struct
import zipfile


def safe_name(name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name:
        raise ValueError("Unsafe archive path")


def package(artifacts, output, version, commit, run_url):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("Invalid release version")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("A full source commit is required")
    output.mkdir(parents=True, exist_ok=True)
    archives = []
    for platform in ("macos-arm64", "macos-x86_64", "windows-x64", "linking-service"):
        source = artifacts / ("Deskchirp-" + platform)
        if not source.is_dir():
            raise ValueError("Missing artifact: " + str(source))
        archive = output / f"Deskchirp-{version}-{platform}{'-portable' if platform == 'windows-x64' else ''}.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as result:
            if platform.startswith("macos-"):
                with zipfile.ZipFile(source / ("Deskchirp-" + platform + ".zip")) as app:
                    if app.testzip() is not None:
                        raise ValueError("Mac app archive failed its CRC check")
                    info = plistlib.loads(app.read("Deskchirp.app/Contents/Info.plist"))
                    if info["CFBundleShortVersionString"] != version.split("-")[0]:
                        raise ValueError("Mac app version does not match release")
                    binary = app.read("Deskchirp.app/Contents/MacOS/Deskchirp")
                    expected_cpu = 0x100000C if platform.endswith("arm64") else 0x1000007
                    if struct.unpack("<II", binary[:8]) != (0xFEEDFACF, expected_cpu):
                        raise ValueError("Wrong Mac CPU architecture")
                    for item in app.infolist():
                        safe_name(item.filename)
                        # Copy ZipInfo too: executable permissions and Mac metadata survive.
                        result.writestr(item, app.read(item))
            if platform == "windows-x64":
                binary = (source / "Deskchirp.exe").read_bytes()
                offset = struct.unpack_from("<I", binary, 60)[0]
                if binary[:2] != b"MZ" or binary[offset:offset + 4] != b"PE\0\0" or struct.unpack_from("<H", binary, offset + 4)[0] != 0x8664:
                    raise ValueError("Wrong Windows CPU architecture")
                for name in ("Deskchirp.dll", "Deskchirp.pri", "coreclr.dll", "Microsoft.ui.xaml.dll", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"):
                    if not (source / name).is_file():
                        raise ValueError("Missing Windows runtime/resource: " + name)
            for path in sorted(source.rglob("*")):
                if not path.is_file():
                    continue
                relative = path.relative_to(source).as_posix()
                safe_name(relative)
                if path.suffix.lower() in (".zip", ".pdb") or path.name.startswith("settings") or path.name == "BUILD-INFO.json":
                    continue
                result.write(path, relative)
            legal_path = "LICENSE"
            if legal_path not in result.namelist() or "THIRD_PARTY_NOTICES.md" not in result.namelist():
                raise ValueError("Redistribution notices are missing")
            if platform == "linking-service" and "coordination/service.py" not in result.namelist():
                raise ValueError("Linking service source is missing")
            metadata = {"name": "Deskchirp for Discord", "version": version, "commit": commit,
                        "platform": platform, "build": run_url,
                        "source": "https://github.com/geekynasir/deskchirp-discord/tree/" + commit}
            result.writestr("BUILD-INFO.json", json.dumps(metadata, indent=2) + "\n")
        with zipfile.ZipFile(archive) as check:
            if check.testzip() is not None:
                raise ValueError("Packaged archive failed its CRC check")
        archives.append(archive)
        print(archive.name, archive.stat().st_size, "bytes")
    installer_dir = artifacts / "Deskchirp-windows-installer"
    installer = installer_dir / f"Deskchirp-{version}-windows-x64-setup.exe"
    provenance = json.loads((installer_dir / "BUILD-INFO.json").read_text(encoding="utf-8-sig"))
    if provenance.get("version") != version or provenance.get("commit") != commit:
        raise ValueError("Windows installer source/version does not match release")
    with installer.open("rb") as stream:
        if stream.read(2) != b"MZ" or installer.stat().st_size < 1_048_576:
            raise ValueError("Windows installer executable is missing or invalid")
    published_installer = output / installer.name
    shutil.copyfile(installer, published_installer)
    archives.append(published_installer)
    print(published_installer.name, published_installer.stat().st_size, "bytes")
    (output / "SHA256SUMS").write_text("".join(
        hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name + "\n" for path in archives
    ))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-url", required=True)
    args = parser.parse_args()
    package(args.artifacts, args.output, args.version, args.commit, args.run_url)
