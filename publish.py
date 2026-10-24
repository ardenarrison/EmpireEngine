#!/usr/bin/env python3
"""Publishes Empire Engine Hub, the editor and the player as two separate packages per platform.

    python3 publish.py                         # for the platform you are on
    python3 publish.py --rid win-x64 --rid linux-x64 --zip

The hub does not come with an editor. People install one from inside the hub (Installs > Install version...), which
downloads the editor package from the GitHub release. The result is two folders in Empire/ for each platform:

    Empire/osx-arm64/                the hub package         ->  EmpireEngineHub-<version>-osx-arm64.zip
        EmpireHub                    the hub (run this)
    Empire/osx-arm64-editor/         the editor package      ->  EmpireEngine-<version>-osx-arm64.zip
        Empire/<version>/            what the hub unpacks into <hub>/Empire/<version>/
            empire                   the editor
            Player/                  the game player

To try the hub with an editor without a release, copy the Empire/ folder from the editor package next to EmpireHub.

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
    destination = out_root / rid                       # the hub package: no editor in it
    editor_root = out_root / f"{rid}-editor"           # the editor package, laid out as the hub unpacks it
    editor_dir = editor_root / "Empire" / version

    # Start clean, so files from an older publish don't end up in the package.
    for folder in (destination, editor_root):
        if folder.exists():
            shutil.rmtree(folder)

    # Publishing the hub leaves the editor out (see PublishEditor in Hub.csproj).
    publish("Hub/Hub.csproj", destination, rid)
    publish("Editor/Editor.csproj", editor_dir, rid)
    # The player goes next to the editor. GameName is the name of the program it builds.
    publish("Player/Player.csproj", editor_dir / "Player", rid, "-p:GameName=Player")

    expected = [destination / ("EmpireHub" + exe), editor_dir / ("empire" + exe), editor_dir / "Player" / ("Player" + exe)]
    if (destination / "Empire").exists():
        sys.exit(f"The hub package must not contain an editor, but {destination / 'Empire'} exists.")
    missing = [path for path in expected if not path.exists()]
    if missing:
        sys.exit("The package is incomplete, these are missing:\n  " + "\n  ".join(str(path) for path in missing))

    problems = []
    if rid == "osx-x64":
        problems.append("Intel Mac builds bundle no Vulkan (the repo only has the Apple Silicon copies), so people need the "
                        "Vulkan SDK or Homebrew's vulkan-loader and molten-vk installed. Say so in the release notes.")
    if rid == "osx-arm64":
        # Apple Silicon builds carry their own Vulkan loader and MoltenVK (from ThirdParty/Vulkan). Without them the editor
        # and games only run on Macs that have Vulkan installed.
        for folder in (editor_dir, editor_dir / "Player"):
            if not (folder / "libvulkan.1.dylib").exists() or not (folder / "vulkan" / "icd.d" / "MoltenVK_icd.json").exists():
                problems.append(f"{folder.relative_to(editor_root)} has no bundled Vulkan (libvulkan.1.dylib, vulkan/icd.d).")
        if problems:
            problems.append("They come from ThirdParty/Vulkan in the repo: restore that folder, or set the EmpireVulkanLoader "
                            "and EmpireMoltenVK environment variables to the paths of libvulkan.1.dylib and libMoltenVK.dylib.")

    if make_zip:
        # The editor zip is the one the hub's Install version... downloads, so its name and layout are fixed:
        # EmpireEngine-<version>-<platform>.zip holding Empire/<version>/ (see Hub/Releases/ReleaseCatalog.cs).
        for name, root in ((f"EmpireEngineHub-{version}-{rid}", destination), (f"EmpireEngine-{version}-{rid}", editor_root)):
            print(f"Zipped: {shutil.make_archive(str(out_root / name), 'zip', root_dir=root)}")
    return destination, problems


def main():
    parser = argparse.ArgumentParser(description="Publish Empire Engine Hub, and the editor with the player, as separate packages in the Empire folder.")
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
