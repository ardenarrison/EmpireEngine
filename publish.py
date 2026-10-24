#!/usr/bin/env python3
"""Publishes Empire Engine Hub, the editor and the player, and packages them into one folder.

    python3 publish.py                         # for the platform you are on
    python3 publish.py --rid win-x64 --rid linux-x64 --zip

The result is Empire/<platform>/, ready to zip and ship:

    Empire/osx-arm64/
        EmpireHub                    the hub (run this)
        Empire/<version>/            the editor, which is where the hub looks for installed editors
            empire                   the editor
            Player/                  the game player

Needs the .NET SDK. Games are built self-contained, so people who run them need nothing installed except, on macOS
and Linux, whatever the editor's build says it couldn't bundle.
"""
import argparse
import platform
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
EXE = ".exe"   # added to program names for Windows platforms


def current_platform():
    """The .NET runtime identifier for this computer, such as osx-arm64 or win-x64."""
    system = platform.system()
    machine = platform.machine().lower()
    arm = machine in ("arm64", "aarch64")
    if system == "Darwin":
        return "osx-arm64" if arm else "osx-x64"
    if system == "Windows":
        return "win-arm64" if arm else "win-x64"
    return "linux-arm64" if arm else "linux-x64"


def empire_version():
    """EmpireVersion from Directory.Build.props, the version shared by the hub, editor and player."""
    text = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<EmpireVersion>\s*([^<\s]+)\s*</EmpireVersion>", text)
    if not match:
        sys.exit("Could not find EmpireVersion in Directory.Build.props.")
    return match.group(1)


def run(command):
    print("\n$ " + " ".join(str(part) for part in command), flush=True)
    subprocess.run([str(part) for part in command], cwd=ROOT, check=True)


def publish(project, destination, rid, *extra):
    run(["dotnet", "publish", ROOT / project, "-c", "Release", "-r", rid, "--self-contained", "true",
         "-o", destination, *extra])


def package(rid, out_root, version, make_zip):
    exe = EXE if rid.startswith("win") else ""
    destination = out_root / rid
    editor_dir = destination / "Empire" / version

    # Start clean, so files from an older publish don't end up in the package.
    if destination.exists():
        shutil.rmtree(destination)

    # The hub's publish also publishes the editor into <hub>/Empire/<version>/ (see Hub.csproj).
    publish("Hub/Hub.csproj", destination, rid)
    # The player goes next to the editor. GameName is the name of the program it builds.
    publish("Player/Player.csproj", editor_dir / "Player", rid, "-p:GameName=Player")

    expected = [destination / ("EmpireHub" + exe), editor_dir / ("empire" + exe), editor_dir / "Player" / ("Player" + exe)]
    missing = [path for path in expected if not path.exists()]
    if missing:
        sys.exit("The package is incomplete, these are missing:\n  " + "\n  ".join(str(path) for path in missing))

    problems = []
    if rid.startswith("osx"):
        # macOS builds carry their own Vulkan loader and MoltenVK (from ThirdParty/Vulkan). Without them the editor
        # and games only run on Macs that have Vulkan installed.
        for folder in (editor_dir, editor_dir / "Player"):
            if not (folder / "libvulkan.1.dylib").exists() or not (folder / "vulkan" / "icd.d" / "MoltenVK_icd.json").exists():
                problems.append(f"{folder.relative_to(destination)} has no bundled Vulkan (libvulkan.1.dylib, vulkan/icd.d).")
        if problems:
            problems.append("They come from ThirdParty/Vulkan in the repo: restore that folder, or set the EmpireVulkanLoader "
                            "and EmpireMoltenVK environment variables to the paths of libvulkan.1.dylib and libMoltenVK.dylib.")

    if make_zip:
        archive = shutil.make_archive(str(out_root / f"Empire-{version}-{rid}"), "zip", root_dir=destination)
        print(f"Zipped: {archive}")
    return destination, problems


def main():
    parser = argparse.ArgumentParser(description="Publish Empire Engine Hub, the editor and the player into the Empire folder.")
    parser.add_argument("--rid", action="append", metavar="PLATFORM",
                        help="platform to build for (osx-arm64, osx-x64, win-x64, win-arm64, linux-x64, linux-arm64). "
                             "Repeat for several. Default: this computer's.")
    parser.add_argument("--out", default="Empire", help="folder the packages go in (default: Empire, next to this script)")
    parser.add_argument("--zip", action="store_true", help="also make a .zip of each package")
    args = parser.parse_args()

    if shutil.which("dotnet") is None:
        sys.exit("The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download and try again.")

    out_root = (ROOT / args.out).resolve()
    if out_root == ROOT or ROOT not in out_root.parents:
        sys.exit(f"--out must be a folder inside {ROOT}, not {out_root}.")

    version = empire_version()
    rids = args.rid or [current_platform()]
    started = time.time()
    warnings = []
    for rid in rids:
        print(f"\n=== Empire Engine {version} for {rid} ===")
        destination, problems = package(rid, out_root, version, args.zip)
        print(f"\nPackaged {destination}")
        warnings += [f"[{rid}] {problem}" for problem in problems]

    print(f"\nDone in {time.time() - started:.0f} seconds. Output: {out_root}")
    if warnings:
        print("\nWARNING:")
        for warning in warnings:
            print("  " + warning)


if __name__ == "__main__":
    main()
