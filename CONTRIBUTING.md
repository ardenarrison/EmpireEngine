# Contributing to Empire Engine

Thank you for helping. This page covers how to build and test, what a good change looks like, and the one thing every
commit needs: a sign-off.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Bugs and small fixes**: just open a pull request. For a bug, say how to reproduce it.
- **New features or big changes**: open an issue first so we can agree on the idea before you spend time on it.
- **Security problems**: don't open a public issue. See [SECURITY.md](SECURITY.md).
- Read the [docs](docs/index.html), especially [Engine architecture](docs/architecture.html).

## Building and testing

You need the .NET 10 SDK and a Vulkan-capable GPU.

```sh
dotnet build EmpireEngine.slnx      # Engine, Editor, Player, Tests, Probe
dotnet build Hub                    # the launcher (separate; a build from source also builds and copies in the editor)
dotnet run --project Tests          # prints PASS or FAIL for each check; exits 1 if any fail
python3 Tools/check_repo.py         # repository checks: licenses, no build output, the BRUTAL splash is intact
```

Before sending a pull request, the build should be clean (no warnings), and every test should pass.

## What a good change looks like

- **Small and focused.** One thing per pull request. Don't mix a fix with unrelated tidying.
- **Matches the code around it.** Same naming, same comment style. Comments in this codebase say *why*, in plain English,
  and sit above the thing they describe.
- **Tested.** Engine behaviour is covered by checks in `Tests/Program.cs` (add yours to the nearest section). Things that
  need a window or a GPU can't be checked there, so say how you tried them.
- **Documented.** If it changes what users see or call, update the matching page in `docs/` (and check the page's links
  with `python3 Tools/check_repo.py`).
- **Cross-platform.** Windows, macOS and Linux. Don't hard-code path separators or assume a case-insensitive file system.

## Rules about dependencies and licenses

Empire Engine is Apache-2.0 and ships inside people's games, so:

- **No copyleft.** Don't add anything under GPL, LGPL, AGPL or similar. Permissive licenses (MIT, BSD, Apache-2.0,
  public domain) are fine.
- **List every new dependency** in [`Licenses/credits.json`](Licenses/credits.json), add its license text to `Licenses/`,
  and say which programs ship it. `Tools/check_repo.py` checks the files exist.
- **Don't add art, models, sounds or fonts** unless you made them or their license allows redistribution in a
  commercial product. State where they came from in the pull request.
- **The BRUTAL splash stays.** The [BRUTAL License](Licenses/BRUTAL-LICENSE.txt) requires the BRUTAL logo in every product
  built with it, so games, the editor and the Hub all open on it. A pull request that removes, hides, shrinks, recolors or skips it will not be accepted.
- **Empire Engine's name and crown logo** are trademarks; see [TRADEMARKS.md](TRADEMARKS.md).

## Signing off your commits (DCO)

Every commit must carry a sign-off line, which says you wrote the change or have the right to submit it under the
project's license. It is the [Developer Certificate of Origin](https://developercertificate.org/), version 1.1: read it
there. Add the line with `-s`:

```sh
git commit -s -m "Fix the terrain brush at the edge"
```

That adds `Signed-off-by: Your Name <you@example.com>`, using your git name and email. If you forgot, fix the last
commit with `git commit --amend -s --no-edit`, or sign off several with `git rebase --signoff main`.

Your contribution is licensed under the same terms as the project, the [Apache License 2.0](LICENSE) (section 5 of that
license), so you keep your copyright and the project can keep shipping your work.

## Sending a pull request

1. Fork the repository and make your change on a branch.
2. Build, run the tests, and run `Tools/check_repo.py`.
3. Open a pull request against `main` and fill in the template. Say what changed and why, and how you tested it.
4. A maintainer will review it. Expect questions and requests for changes; that's normal and not a judgement of you.
