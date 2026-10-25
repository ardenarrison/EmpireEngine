#!/usr/bin/env python3
"""Checks the repository is in shape to be public, and stays that way. Run it before sending a change and in CI:

    python3 Tools/check_repo.py              # fails on real problems; placeholders are reported as warnings
    python3 Tools/check_repo.py --publish    # also fails while any [CONTACT EMAIL] placeholder is left (before going public)
    python3 Tools/check_repo.py --history    # also scans every commit for build output, big files and secrets

What it checks:
  - the license, notice and community files exist
  - no build output (bin/, obj/), .DS_Store or very large files are tracked
  - no personal paths (/Users/<name>, C:\\Users\\<name>), personal email addresses or secret-looking strings are in tracked files
  - every third-party component in Licenses/credits.json has its license file, and its text names the right holder
  - the license is the Apache 2.0 text
  - the BRUTAL splash is still there in games, the editor and the hub: the BRUTAL License (section 2.2) requires the BRUTAL logo
  - the docs' internal links and anchors resolve
"""
import json
import re
import subprocess
import sys
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PLACEHOLDER = "[CONTACT EMAIL]"
MAX_FILE_MB = 12         # the biggest legitimate file is the vendored Intel Mac shaderc library, about 10 MB
REQUIRED = ["LICENSE", "NOTICE", "README.md", "CONTRIBUTING.md", "CODE_OF_CONDUCT.md", "SECURITY.md", "TRADEMARKS.md",
            "Licenses/BRUTAL-LICENSE.txt", "Licenses/NOTICE.txt", "Licenses/credits.json"]

errors, warnings = [], []


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=True).stdout


def tracked():
    return [p for p in git("ls-files", "-z").split("\0") if p]


def text_files(paths):
    for p in paths:
        path = ROOT / p
        if not path.is_file() or path.stat().st_size > 2_000_000:
            continue
        try:
            yield p, path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue          # binary


def check_files(paths):
    for name in REQUIRED:
        if not (ROOT / name).is_file():
            errors.append(f"missing {name}")
    for p in paths:
        if re.search(r"(^|/)(bin|obj)/|\.DS_Store$|\.user$|\.suo$|(^|/)imgui\.ini$", p):
            errors.append(f"build output or editor junk is tracked: {p}")
        path = ROOT / p
        if path.is_file() and path.stat().st_size > MAX_FILE_MB * 1_000_000:
            errors.append(f"{p} is {path.stat().st_size / 1e6:.1f} MB (limit {MAX_FILE_MB} MB): keep big files out of git")


def check_text(paths):
    personal = re.compile(r"/Users/[A-Za-z0-9._-]+/|[A-Za-z]:\\+Users\\+[A-Za-z0-9._-]+|/home/[a-z0-9._-]+/")
    secrets = re.compile(
        r"AKIA[0-9A-Z]{16}|sk-ant-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|xox[baprs]-[A-Za-z0-9-]{10,}"
        r"|(?i:(?:api[_-]?key|secret|passwd|password)\s*[:=]\s*[\"'][^\"']{8,}[\"'])")
    for p, text in text_files(paths):
        if p == "Tools/check_repo.py":
            continue
        for n, line in enumerate(text.splitlines(), 1):
            if personal.search(line) and not p.startswith("Licenses/"):
                errors.append(f"{p}:{n}: a personal path is in the file")
            if secrets.search(line):
                errors.append(f"{p}:{n}: looks like a secret")


def check_emails(paths):
    """Personal email addresses become public with the repository, so none belong in it. Allowed: GitHub no-reply
    addresses, example.com, and the authors named inside third-party license texts under Licenses/."""
    address = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
    allowed = re.compile(r"@users\.noreply\.github\.com$|@example\.(com|org)$")
    for p, text in text_files(paths):
        if p.startswith("Licenses/") or p == "Tools/check_repo.py":
            continue
        for n, line in enumerate(text.splitlines(), 1):
            for m in address.finditer(line):
                if not allowed.search(m.group(0)):
                    errors.append(f"{p}:{n}: an email address ({m.group(0)}) is in the file; use a GitHub no-reply address or remove it")


def check_placeholders(paths, publish):
    found = [p for p, text in text_files(paths) if PLACEHOLDER in text and p != "Tools/check_repo.py"]
    if found:
        (errors if publish else warnings).append(f"{PLACEHOLDER} is still in: {', '.join(found)}")


def check_credits():
    credits = json.loads((ROOT / "Licenses/credits.json").read_text(encoding="utf-8"))
    for entry in credits["entries"]:
        file = ROOT / "Licenses" / entry["file"]
        if not file.is_file():
            errors.append(f"Licenses/credits.json lists {entry['name']} but Licenses/{entry['file']} is missing")
        elif file.stat().st_size < 200:
            errors.append(f"Licenses/{entry['file']} is too short to be a license text")


def check_license():
    text = (ROOT / "LICENSE").read_text(encoding="utf-8")
    for needle in ("Apache License", "Version 2.0, January 2004", "TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION",
                   "END OF TERMS AND CONDITIONS"):
        if needle not in text:
            errors.append(f"LICENSE does not look like the Apache License 2.0 text (missing: {needle})")


def check_splash():
    logo = ROOT / "Engine.Brutal/Ui/Logos/BrutalLogo.png"
    if not logo.is_file() or logo.stat().st_size < 5_000:
        errors.append("the BRUTAL logo (Engine.Brutal/Ui/Logos/BrutalLogo.png) is missing: the BRUTAL License requires it")
    player = (ROOT / "Engine.Brutal/BrutalPlayer.cs").read_text(encoding="utf-8")
    if "SplashScreen.Run(" not in player:
        errors.append("BrutalPlayer no longer runs SplashScreen: every game must show the BRUTAL logo before gameplay")
    splash = (ROOT / "Engine.Brutal/Ui/SplashScreen.cs").read_text(encoding="utf-8")
    for needle in ("BrutalLogo.png", "Copyright (c) 2024 Ahwoo Limited"):
        if needle not in splash:
            errors.append(f"SplashScreen.cs no longer mentions {needle}")
    csproj = (ROOT / "Engine.Brutal/Engine.Brutal.csproj").read_text(encoding="utf-8")
    if "BrutalLogo.png" not in csproj:
        errors.append("Engine.Brutal.csproj no longer embeds BrutalLogo.png")
    # The editor and the hub show it too.
    if "SplashScreen.Run(" not in (ROOT / "Editor/EditorApp.cs").read_text(encoding="utf-8"):
        errors.append("the editor no longer runs SplashScreen: it must show the BRUTAL logo when it opens")
    hub_xaml = (ROOT / "Hub/MainWindow.axaml").read_text(encoding="utf-8")
    if 'x:Name="Splash"' not in hub_xaml or "BrutalLogo.png" not in hub_xaml:
        errors.append("the hub's opening screen (Splash in Hub/MainWindow.axaml) no longer shows the BRUTAL logo")
    if "ShowSplash()" not in (ROOT / "Hub/MainWindow.axaml.cs").read_text(encoding="utf-8"):
        errors.append("the hub no longer calls ShowSplash when it opens")
    if "BrutalLogo.png" not in (ROOT / "Hub/Hub.csproj").read_text(encoding="utf-8"):
        errors.append("Hub.csproj no longer includes BrutalLogo.png")


class _Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links, self.ids, self.heading, self.text, self.headings = [], set(), None, "", []

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "a" and "href" in a:
            self.links.append(a["href"])
        if "id" in a:
            self.ids.add(a["id"])
        if tag in ("h2", "h3"):
            self.heading, self.text = tag, ""

    def handle_endtag(self, tag):
        if tag == self.heading:
            self.headings.append(self.text.strip())
            self.heading = None

    def handle_data(self, data):
        if self.heading:
            self.text += data


def check_docs():
    slug = lambda t: re.sub(r"^-|-$", "", re.sub(r"[^a-z0-9]+", "-", t.lower()))
    pages = {}
    for f in (ROOT / "docs").glob("*.html"):
        page = _Page()
        page.feed(f.read_text(encoding="utf-8"))
        ids = set(page.ids)
        for h in page.headings:          # the site's script gives each heading an id the same way
            base, n = slug(h), 2
            candidate = base
            while candidate in ids:
                candidate, n = f"{base}-{n}", n + 1
            ids.add(candidate)
        pages[f.name] = (page, ids)
    nav = re.findall(r'\["([\w-]+\.html)", "', (ROOT / "docs/assets/site.js").read_text(encoding="utf-8"))
    for n in nav:
        if n not in pages:
            errors.append(f"docs/assets/site.js lists {n} but the page doesn't exist")
    for n in pages:
        if n not in nav:
            errors.append(f"docs/{n} is not in the docs' page list (docs/assets/site.js)")
    for name, (page, _) in pages.items():
        for href in page.links:
            if href.startswith(("http", "mailto")):
                continue
            file, _, frag = href.partition("#")
            target = file or name
            if target not in pages:
                errors.append(f"docs/{name}: broken link {href}")
            elif frag and frag not in pages[target][1]:
                errors.append(f"docs/{name}: broken anchor {href}")


def check_history():
    big = []
    out = subprocess.run("git rev-list --objects --all | git cat-file --batch-check='%(objecttype) %(objectname) %(objectsize) %(rest)'",
                         cwd=ROOT, shell=True, capture_output=True, text=True).stdout
    junk = re.compile(r"(^|/)(bin|obj)/|\.DS_Store$")
    for line in out.splitlines():
        parts = line.split(" ", 3)
        if len(parts) == 4 and parts[0] == "blob":
            size, path = int(parts[2]), parts[3]
            if junk.search(path) or size > MAX_FILE_MB * 1_000_000:
                big.append((size, path))
    if big:
        total = sum(s for s, _ in big)
        warnings.append(f"history holds {len(big)} build-output or oversized files ({total / 1e6:.0f} MB) that are no longer in the "
                        "tree; consider cleaning the history before publishing (see the open-source checklist)")


def main():
    publish, history = "--publish" in sys.argv, "--history" in sys.argv
    paths = tracked()
    check_files(paths)
    check_text(paths)
    check_emails(paths)
    check_placeholders(paths, publish)
    check_license()
    check_credits()
    check_splash()
    check_docs()
    if history:
        check_history()
    for w in warnings:
        print("warning:", w)
    for e in errors:
        print("ERROR:  ", e)
    print(f"\n{len(errors)} error(s), {len(warnings)} warning(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
