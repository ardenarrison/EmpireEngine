#!/usr/bin/env python3
"""Publishes Empire Engine Hub, the editor and the player as two separate packages per platform.

    python3 publish.py                         # for the platform you are on
    python3 publish.py --rid win-x64 --rid linux-x64 --zip

The hub does not come with an editor. People install one from inside the hub (Installs > Install version...), which
downloads the editor package from the GitHub release. The result is two folders in Empire/ for each platform:

    Empire/win-x64/                  the hub package         ->  EmpireEngineHub-<version>-win-x64.zip
        EmpireHub.exe                the hub (run this); Linux has EmpireHub
    Empire/osx-arm64/                the hub package on macOS ->  EmpireEngineHub-<version>-osx-arm64.zip
        Empire Engine Hub.app        the hub, as a Mac app (drag it into Applications)
    Empire/osx-arm64-editor/         the editor package      ->  EmpireEngine-<version>-osx-arm64.zip
        Empire/<version>/            what the hub unpacks into <hub>/Empire/<version>/
            empire                   the editor            (on macOS: Empire Engine Editor.app, with the editor and
            Player/                  the game player        the player inside it, in Contents/MacOS)

To try the hub with an editor without a release, copy the Empire/ folder from the editor package next to EmpireHub (on macOS the
hub keeps editors in ~/Library/Application Support/EmpireHub/Empire, so copy it there instead).

Needs the .NET SDK. Games are built self-contained, so people who run them need nothing installed except, on macOS
and Linux, whatever the editor's build says it couldn't bundle.
"""
import argparse
import platform
import plistlib
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


APP_NAME = "Empire Engine Hub"
EDITOR_APP_NAME = "Empire Engine Editor"


def make_app_bundle(files, destination, version, name=APP_NAME, executable="EmpireHub", identifier="io.github.ardenarrison.empireenginehub"):
    """Turns the published program in `files` into <name>.app inside `destination` (macOS).

    An app is a folder: Contents/MacOS holds the program and everything it needs, Contents/Resources the icon, and
    Contents/Info.plist says what the app is. It is signed ad hoc (no developer account is needed) so macOS accepts it as one
    sealed thing; that needs the codesign tool, which only exists on a Mac.
    """
    app = destination / f"{name}.app"
    macos, resources = app / "Contents" / "MacOS", app / "Contents" / "Resources"
    resources.mkdir(parents=True)
    shutil.copytree(files, macos, symlinks=True)
    # macOS code signing takes a folder named like icd.d (a dot and a short extension) for a bundle it doesn't know and refuses to sign
    # the app, so the Vulkan driver folders are named icd inside the app. The editor and games look for either name.
    for driver_folder in list(macos.rglob("icd.d")):
        driver_folder.rename(driver_folder.with_name("icd"))
    shutil.copy2(ROOT / "EmpireEngineIcon.icns", resources / "EmpireEngineIcon.icns")
    numeric = re.match(r"\d+(\.\d+){0,2}", version)
    info = {
        "CFBundleName": name,
        "CFBundleDisplayName": name,
        "CFBundleIdentifier": identifier,
        "CFBundleExecutable": executable,
        "CFBundleIconFile": "EmpireEngineIcon",
        "CFBundlePackageType": "APPL",
        "CFBundleSignature": "????",
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleShortVersionString": version,
        "CFBundleVersion": numeric.group(0) if numeric else version,
        "LSApplicationCategoryType": "public.app-category.developer-tools",
        "NSHighResolutionCapable": True,
        "NSHumanReadableCopyright": "Copyright 2026 Arden Arrison. Apache License 2.0.",
    }
    with open(app / "Contents" / "Info.plist", "wb") as f:
        plistlib.dump(info, f)

    problems = []
    if shutil.which("codesign") and sys.platform == "darwin":
        sign = ["codesign", "--force", "--deep", "--sign", "-", "--timestamp=none", str(app)]
        if subprocess.run(sign, capture_output=True, text=True).returncode != 0:
            problems.append(f"{app.name} could not be signed (codesign failed).")
        elif subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], capture_output=True, text=True).returncode != 0:
            problems.append(f"{app.name} was signed but doesn't verify.")
    else:
        problems.append(f"{app.name} was not signed, because signing needs a Mac. It will open on a Mac that allows unsigned apps, "
                        "but build the release on a Mac so the app is signed.")
    return app, problems


def package(rid, out_root, version, make_zip):
    exe = EXE if rid.startswith("win") else ""
    destination = out_root / rid                       # the hub package: no editor in it
    editor_root = out_root / f"{rid}-editor"           # the editor package, laid out as the hub unpacks it
    editor_dir = editor_root / "Empire" / version

    mac = rid.startswith("osx")
    hub_files = out_root / f"{rid}-hub-files"          # on macOS the hub is published here first and then put inside the app

    # Start clean, so files from an older publish don't end up in the package.
    for folder in (destination, editor_root, hub_files):
        if folder.exists():
            shutil.rmtree(folder)

    # Publishing the hub leaves the editor out (see PublishEditor in Hub.csproj).
    publish("Hub/Hub.csproj", hub_files if mac else destination, rid)
    problems = []
    if mac:
        destination.mkdir(parents=True)
        app, app_problems = make_app_bundle(hub_files, destination, version)
        problems += app_problems
        shutil.rmtree(hub_files)
        hub_exe = app / "Contents" / "MacOS" / "EmpireHub"
        hub_must_not_have = app / "Contents" / "MacOS" / "Empire"
    else:
        hub_exe = destination / ("EmpireHub" + exe)
        hub_must_not_have = destination / "Empire"
    # The player goes next to the editor. GameName is the name of the program it builds. On macOS the editor and the player are
    # published into a plain folder first and then put inside Empire Engine Editor.app.
    editor_files = out_root / f"{rid}-editor-files" if mac else editor_dir
    if editor_files.exists():
        shutil.rmtree(editor_files)
    publish("Editor/Editor.csproj", editor_files, rid)
    publish("Player/Player.csproj", editor_files / "Player", rid, "-p:GameName=Player")
    if mac:
        editor_dir.mkdir(parents=True)
        editor_app, editor_problems = make_app_bundle(editor_files, editor_dir, version, EDITOR_APP_NAME, "empire",
                                                      "io.github.ardenarrison.empireengine.editor")
        problems += editor_problems
        shutil.rmtree(editor_files)
        editor_dir = editor_app / "Contents" / "MacOS"   # from here on, where the editor's files are

    expected = [hub_exe, editor_dir / ("empire" + exe), editor_dir / "Player" / ("Player" + exe)]
    if hub_must_not_have.exists():
        sys.exit(f"The hub package must not contain an editor, but {hub_must_not_have} exists.")
    missing = [path for path in expected if not path.exists()]
    if missing:
        sys.exit("The package is incomplete, these are missing:\n  " + "\n  ".join(str(path) for path in missing))

    if rid == "osx-x64":
        problems.append("Intel Mac builds bundle no Vulkan (the repo only has the Apple Silicon copies), so people need the "
                        "Vulkan SDK or Homebrew's vulkan-loader and molten-vk installed. Say so in the release notes.")
    if rid == "osx-arm64":
        # Apple Silicon builds carry their own Vulkan loader and MoltenVK (from ThirdParty/Vulkan). Without them the editor
        # and games only run on Macs that have Vulkan installed.
        vulkan_problems = []
        for folder in (editor_dir, editor_dir / "Player"):
            if not (folder / "libvulkan.1.dylib").exists() or not any((folder / "vulkan" / name / "MoltenVK_icd.json").exists() for name in ("icd.d", "icd")):
                vulkan_problems.append(f"{folder.relative_to(editor_root)} has no bundled Vulkan (libvulkan.1.dylib, vulkan/icd.d or vulkan/icd).")
        if vulkan_problems:
            problems += vulkan_problems
            problems.append("They come from ThirdParty/Vulkan in the repo: restore that folder, or set the EmpireVulkanLoader "
                            "and EmpireMoltenVK environment variables to the paths of libvulkan.1.dylib and libMoltenVK.dylib.")

    if make_zip:
        # The editor zip is the one the hub's Install version... downloads, so its name and layout are fixed:
        # EmpireEngine-<version>-<platform>.zip holding Empire/<version>/ (see Hub/Releases/ReleaseCatalog.cs).
        for name, root in ((f"EmpireEngineHub-{version}-{rid}", destination), (f"EmpireEngine-{version}-{rid}", editor_root)):
            if mac and shutil.which("ditto") and sys.platform == "darwin":
                # The apps' signatures are kept in file attributes, which an ordinary zip drops (an app would then show up as
                # "damaged" on the Mac that downloads it). ditto is macOS's own zip tool and keeps them, so Finder restores them.
                # It zips one folder, with that folder's name at the top: the Hub app, or the editor package's Empire folder.
                archive = out_root / f"{name}.zip"
                archive.unlink(missing_ok=True)
                top = destination / f"{APP_NAME}.app" if root == destination else editor_root / "Empire"
                run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", top, archive])
                print(f"Zipped: {archive}")
            else:
                if mac:
                    problems.append("A macOS zip was made without macOS's ditto, so it lost the app's signature and macOS will call the "
                                    "app damaged. Make the release zips on a Mac.")
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
