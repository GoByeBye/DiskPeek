# Installation and updates

DiskPeek is available as a Windows x64 installer and a self-contained portable
ZIP. Both include the application, read-only scan worker, CLI, corresponding
source, AGPL license, and dependency notices. Windows 10/11 x64 is required.

## Install

Download `DiskPeek-setup-win-x64.exe` from the latest stable GitHub release and
run it. Velopack installs DiskPeek for the current Windows account and adds a
Start menu entry and a Windows Installed apps entry. Installation and updates
do not need administrator access. Fast raw NTFS scans still request elevation
for the separate read-only scan worker.

The installer is not code-signed. A publisher certificate is not bundled with
the project. GitHub release assets include SHA-256 checksums for verification.

## Update

Installed copies check the public `GoByeBye/DiskPeek` GitHub release feed in the
background at most once per day. The check sends a normal request to GitHub;
scan paths and file listings are not sent. Only newer stable Windows x64
versions are offered. Prereleases and downgrades are excluded.

Choose **Updates** to check immediately or disable background checks. When an
update is available, download it in that window, then choose **Update and
restart** to install it. Downloading alone does not approve installation; normal
app startup and exit do not automatically apply a staged update. Package
integrity is verified by Velopack before installation.

Installation waits until scanning and CSV export have finished. Scan results
are held in memory, so export any results you want to retain before restarting.
Theme and background-check preferences are stored in
`%APPDATA%\DiskPeek\settings.json`, outside the installed application files.
An unavailable network or update service does not prevent scanning.

The first installer release is v0.2.0. Older portable copies do not contain an
updater; install a current release once to receive subsequent in-app updates.
For a portable copy, the Updates window opens the installer download rather
than replacing files in an arbitrary directory.

## Uninstall

Use Windows **Settings → Apps → Installed apps → DiskPeek → Uninstall**. User
preferences live separately from the app and may be removed manually by deleting
`%APPDATA%\DiskPeek` if they are no longer needed. Scanned files are never changed
by installation, updating, or uninstalling DiskPeek.

## Release maintenance

The application library and `vpk` packaging tool both use Velopack 1.2.161. The
build script restores the pinned packaging tool beneath `work/`; it does not
install a global tool. The update channel is `win-x64`, and the public feed is
served through GitHub Releases. Every release must include its generated
`releases.win-x64.json` and full update package alongside the installer and
source. Keep their generated filenames intact so feed references resolve.

See [CI and releases](CI.md) for publishing instructions. Portable and installer
aliases are byte-identical to the corresponding versioned release assets.
See [installer validation](INSTALLER-VALIDATION.md) for the native update tests
and their limits.
