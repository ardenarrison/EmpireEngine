#!/usr/bin/env bash
# Makes a cleaned copy of this repository's history for publishing: drops the build output (bin/, obj/) and .DS_Store files
# that early commits included, which shrinks a clone from about 60 MB to under 4 MB. Optionally also rewrites the author
# email on every commit (git commit emails are public once a repository is).
#
#     Tools/clean_history.sh <output-folder> [--email 12345+yourname@users.noreply.github.com] [--also-remove 'MyScene/*']
#
# --also-remove drops more paths from every commit (a glob; repeat it for several). Use it for files that were removed
# from the tree but would still be public in old commits, such as the MyScene/ sample project.
# Remove those files from the tree and commit that first: the script checks the cleaned files match your newest commit.
#
# It never touches this repository: it clones it into <output-folder>, rewrites that clone, then checks the newest
# commit's files are byte-for-byte what you have here. Commit hashes change, but every commit, message and date is kept.
# When it's done it prints how to publish the clean copy (into a new, empty repository, never over an old one). Needs git-filter-repo (pip install git-filter-repo).
set -euo pipefail

out="${1:?usage: Tools/clean_history.sh <output-folder> [--email <address>] [--also-remove <glob>]...}"
shift
email=""
extra=()
while [ $# -gt 0 ]; do
  case "$1" in
    --email) email="${2:?--email needs an address}"; shift 2 ;;
    --also-remove) extra+=(--path-glob "${2:?--also-remove needs a path glob}"); shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done
here="$(git rev-parse --show-toplevel)"
[ -e "$out" ] && { echo "$out already exists; choose a new folder." >&2; exit 1; }
git filter-repo --version >/dev/null 2>&1 || { echo "git-filter-repo is not installed (pip install git-filter-repo)." >&2; exit 1; }
[ -z "$(git -C "$here" status --porcelain)" ] || { echo "Commit or stash your changes here first." >&2; exit 1; }

git clone --no-hardlinks "$here" "$out"
cd "$out"
before="$(git count-objects -vH | awk '/size-pack/ {print $2 $3}')"

args=(--force --invert-paths --path-glob '*/bin/*' --path-glob '*/obj/*' --path-glob '*/.DS_Store' --path-glob '.DS_Store' "${extra[@]}")
if [ -n "$email" ]; then
  name="$(git -C "$here" log -1 --format=%an)"
  old="$(git -C "$here" log --format=%ae | sort -u)"
  mailmap="$(mktemp)"
  while read -r address; do echo "$name <$email> <$address>" >> "$mailmap"; done <<< "$old"
  args+=(--mailmap "$mailmap")
fi
git filter-repo "${args[@]}"
git reflog expire --expire=now --all
git gc -q --prune=now --aggressive

[ "$(git rev-parse 'HEAD^{tree}')" = "$(git -C "$here" rev-parse 'HEAD^{tree}')" ] \
  || { echo "The cleaned files differ from yours; something other than build output was removed. Not publishing this." >&2; exit 1; }
echo
echo "Cleaned copy: $out"
echo "  size $before -> $(git count-objects -vH | awk '/size-pack/ {print $2 $3}'), $(git rev-list --count HEAD) commits, files identical to yours."
echo "  Check it: cd '$out' && python3 Tools/check_repo.py --history"
echo "  Publish it into a BRAND NEW, empty repository (create it on GitHub with no README, license or .gitignore):"
echo "    git remote add origin https://github.com/<you>/<repo>.git   # filter-repo removes the origin remote"
echo "    git push origin main"
echo "  Don't force-push over a repository that has had pull requests (Dependabot opens them). GitHub keeps every pull"
echo "  request's commits downloadable under refs/pull/ even after its branch is deleted, so the old history, with the old"
echo "  email, would still be public once the repository is. Delete that repository and make a fresh one with the same name."
