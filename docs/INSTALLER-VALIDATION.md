# Installer and updater validation — v0.2.0

Validated on Windows x64 on 2026-11-04 with .NET SDK 10.0.403 and Velopack
1.2.161. The production verification suite passed **33 tests, 0 failures,
0 skipped**. Build artifacts are checked by `scripts/verify-release.ps1`.

## Application policy and preferences

Tests exercise the production update service and settings store without opening
WPF or using the current user's preferences. They cover notification-only checks,
separate download and restart approval, active scan/export protection, portable
mode, stable/newer versions, changing package metadata, offline and cancellation
failures, serialized operations, staged/cached payload corruption, daily checks,
clock changes, settings persistence, and malformed or oversized settings recovery.

The actual WPF Updates window was rendered offscreen in light and dark themes,
with both available-update and download-ready states. Text and buttons fit at
570 device-independent pixels. No desktop input or automatic update was used for
those layout checks.

## Native installation and update

An isolated console probe uses the same pinned Velopack library and bootstrap
setting that disables automatic application. It packages a unique application ID,
installs below `work/`, creates no shortcuts, and uses a temporary HKCU uninstall
entry. It does not overwrite a normal DiskPeek installation.

The real native installer/updater passed these checks:

1. Silent, per-user installation of 0.2.0 without elevation.
2. Rejection of a corrupted 0.2.1 update while keeping 0.2.0 intact.
3. A version check without downloading or staging an update.
4. Download and staging of 0.2.1 while the installed app remained 0.2.0.
5. A normal subsequent startup still running 0.2.0, with no automatic apply.
6. The actual `ApplyUpdatesAndRestart` SDK call returning success, with a fresh
   restarted process, application files, assembly version, and uninstall
   registration advancing to 0.2.1.
7. Rejection of a downgrade to an older 0.2.0 feed.
8. Silent uninstall removing the test application and its unique registration.

All 11 native checks passed when the reusable script was run end to end. The
probe and instructions are in `tests/InstallerSmoke/`. This test
checks native update mechanics; application consent and scan/export policy are
covered separately by the production-service tests.

## Package and CI checks

Both installer and portable aliases match their versioned assets byte for byte.
The native package's identity, version, channel, runtime, entry point, worker,
CLI, source, and licensing files are checked. The feed's SHA-256, SHA-1, package
name, and length must match the actual package. The ordinary portable ZIP must
contain no installation marker or updater executable.

The Linux publication validator was exercised against the actual generated
assets, including coherent-but-wrong-version feed and unexpected-file cases.
The write-token release job independently validates assets before publication.

## Limits

The native smoke fixture avoids interactive Windows dialogs. It does not verify
SmartScreen, enterprise application-control policies, concurrent external file
locks, every supported Windows build, or the optional elevated NTFS worker after
an installed update. Releases are unsigned. Existing scan-engine validation
limits remain in [VALIDATION.md](VALIDATION.md).
