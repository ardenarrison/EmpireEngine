# Security policy

## Reporting a problem

**Please don't report security problems in a public issue.** Use GitHub's private reporting instead: on the repository's
**Security** tab, choose **Report a vulnerability**. Only the maintainers can see what you send.

Say what you found, how to reproduce it, and which version and operating system. You'll get a reply within a few days.
We'll work with you on a fix and credit you in the release notes if you'd like.

If the Security tab doesn't offer that option, open a public issue that says only that you have a security problem to
report privately, with **no details**, and a maintainer will arrange a private way to talk.

## Supported versions

Only the latest release and the `main` branch get security fixes. Empire Engine is early software (0.x).

## What counts, and what is by design

**Projects run code.** Empire Engine compiles a project's C# scripts, and any installed packages, and runs them when you press
Play, with your user account's permissions. A script can read your files, use the network and start programs, exactly like
any program you run. So:

- **Only open projects, scenes and packages from people you trust**, the same way you would only run a program you trust.
- A project that runs code when you open and play it is **not** a vulnerability. Empire Engine has no sandbox.

Things we do want to hear about:

- A scene, model, image, sound or save file that crashes the editor or a game in a way that looks exploitable
  (memory corruption, for example), or that runs code without playing the project's scripts.
- The editor, Hub or a built game doing something with your files or network that a project's scripts didn't ask for.
- Build Project or `publish.py` including something in a build that it shouldn't (secrets, local paths).
