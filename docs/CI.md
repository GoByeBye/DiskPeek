# Build and release automation

The `Build and release` GitHub Actions workflow runs on pull requests to `main`,
pushes to `main`, version-tag pushes, and manual **Run workflow** requests.
Pushes to `main` and manual runs with a blank `release_tag` build downloadable
artifacts. A manual run with an explicit release tag also publishes a release.

## Continuous integration

The Windows 2025 x64 job reads the exact SDK version from `global.json`, installs
it with the official `actions/setup-dotnet` action in an isolated directory, and
checks the selected SDK. It runs the same build/test/package entry point used
locally:

```powershell
.\scripts\build.ps1 -DotnetExe (Get-Command dotnet).Source -OutputDirectory outputs/ci
```

The console verification runner must exit successfully. CI also requires a fresh
`verification-results.json` with at least one passing test and zero failures,
all seven release assets, and matching entries in `SHA256SUMS.txt`. CI checks
both byte-identical aliases, the installer's product version, the native package
identity/version and contents, and the feed's package name, hashes, and size.
Skipped environment-dependent tests remain visible in the report and run summary.

After a successful run, open **Actions → Build and release → the run → Artifacts**.
The `DiskPeek-<version>-win-x64-<run>-<attempt>` artifact contains:

- `DiskPeek-<version>-win-x64.zip`: the self-contained portable Windows app.
- `DiskPeek-<version>-source.zip`: the corresponding source, including CI files.
- `DiskPeek-portable-win-x64.zip`: a byte-identical copy of the versioned portable ZIP.
- `DiskPeek-<version>-setup-win-x64.exe`: the per-user Windows installer.
- `DiskPeek-setup-win-x64.exe`: a byte-identical, stable-name installer alias.
- `DiskPeek-<version>-win-x64-full.nupkg`: the native full update package.
- `releases.win-x64.json`: the native update feed for the `win-x64` channel.
- `SHA256SUMS.txt`: SHA-256 checksums for exactly those seven release assets.

Extract the Actions artifact, then extract the portable app ZIP before launching
`DiskPeek.exe`. The separate `verification-<run>-<attempt>` artifact retains the
test report, including on failed builds when the runner produced it. Artifacts
are retained for 30 days, subject to repository policy. A build failing before
tests start has no test report; this is reported as a missing diagnostics file.

Hosted CI does not run the interactive WPF benchmark or accept UAC prompts.
It does render the actual treemap offscreen on STA threads to verify that tiny
deletions leave neighboring pixels unchanged and collapsed groups show only the
deleted byte proportion, including tiles four DIPs thick in either orientation.
Live elevated scans, GUI interaction, and external storage/provider scenarios
retain the verification limits described in `VALIDATION.md`.

## Publishing a release

1. Set `Version` in `Directory.Build.props`, commit the change, and merge to `main`.
2. Open **Actions → Build and release → Run workflow**, select `main`, and enter
   the matching `release_tag`, such as `v0.1.1` or `v0.2.0-rc.1`. Alternatively,
   create and push an annotated tag matching the project version.
3. Wait for the Windows build and verification to pass. The release job checks
   the downloaded artifact's integrity and the package checksums, creates a draft
   GitHub release, attaches the seven packages/feed assets and `SHA256SUMS.txt`,
   then publishes it. The updater needs the full `.nupkg` and matching feed in
   that same release; they keep their native filenames.

A mismatched or malformed `v*` tag fails before building. Versions containing a
prerelease suffix are published as GitHub prereleases and explicitly excluded from
GitHub's latest-release link. Stable releases use GitHub's automatic `legacy`
latest-selection policy, which considers release creation dates and higher
semantic versions. A later-published backport is not unconditionally forced latest.
The publish request applies draft, prerelease, and latest policy together.
Leave `release_tag` blank for a build-only manual run. No release is made simply
by pushing the repository for the first time.

An explicit manual release must run its workflow from `main` and match
`Directory.Build.props` at the selected source commit. A separate API-only job
validates that metadata and creates an annotated tag at that source commit.
An existing tag is accepted only if it already resolves to that exact commit;
tags are never moved. The read-only Windows job checks out that commit, builds,
tests, and packages it before publication. The workflow continues directly to
its release job because tags created using its automatic token do not trigger a
second tag-push workflow. If verification fails, the tag remains available for
inspection or retrying the run; the release is not published.

The optional `release_commit` input selects an exact 40-character source commit
SHA. Leave it blank to build the dispatched `main` commit. For a retry after a
workflow-only fix, supply the existing tag's source SHA: the updated workflow on
`main` rebuilds and publishes that original source without moving the tag. Source
version, checkout, artifacts, and the final publication tag check all use the
selected source SHA. Automated callers can pin their intended source in this way.
The narrowly scoped `release-v0.1.1.yml` and `release-v0.2.0.yml` request helpers
invoke this same path with both inputs; each has
only `actions: write` and `contents: read`, and does not create tags or releases.

The release job locates drafts by exact tag across the paginated releases
collection, rejects ambiguous matches, and can resume an existing draft after an
interrupted upload. It
refuses to overwrite an already published release; use a new version/tag for
changed binaries. Releases keep their downloadable assets independently of the
30-day Actions-artifact retention window.

## Automatic portable download link

The README's portable download uses this stable release-asset URL:

[Download the latest stable Windows x64 portable ZIP](https://github.com/GoByeBye/DiskPeek/releases/latest/download/DiskPeek-portable-win-x64.zip)

Every new release includes that exact asset name alongside the versioned portable
and source ZIPs. The README link automatically serves the portable ZIP from
GitHub's latest stable release; no README version edit is required. Stable
publication uses GitHub's date/version-based selection rather than forcing an
older backport to latest. Prerelease publication leaves the stable download link alone.
Versioned assets and their corresponding source remain available on each release;
the pipeline does not replace assets in an already published release.

The initial v0.1.0 release can gain the alias by adding an identical copy of its
existing portable ZIP. Its original versioned ZIPs and checksum manifest remain
unchanged; the existing portable checksum also verifies the identical alias bytes.

## Per-user installer and update packages

Packaging pins **Velopack vpk 1.2.161** in `.config/dotnet-tools.json`.
`scripts/install-vpk.ps1` installs that exact tool into `work/vpk`, with NuGet and
CLI caches also kept under `work`. No global tool or Visual Studio installation
is needed. The app references the matching Velopack library version.

`scripts/build.ps1` creates the ordinary portable/source ZIPs first. It copies
the portable directory into a new `work/installer-packaging` staging directory,
then runs `vpk pack` with ID `DiskPeek`, the project version, entry point
`DiskPeek.exe`, runtime/channel `win-x64`, `--delta None`, and `--noPortable`.
The generated Setup executable installs for the current user under
`%LocalAppData%\DiskPeek` and creates a Start Menu shortcut. It requires no
administrator prompt for installation; an elevated scan remains a separate
explicit operation in the application. MSI and per-machine installation are
not part of this release.

The plain portable ZIP remains an ordinary extract-and-run bundle with no
`sq.version`, `Update.exe`, or Velopack portable marker. Installed copies include
the native installation metadata needed by Velopack. Both forms include the
corresponding source ZIP, AGPL license, and dependency notices. Do not store
personal data in the installed `current` directory: updates replace it.

The installed app uses the stable GitHub release feed and the explicit
`win-x64` channel. It notifies the user before downloading or applying an update;
packaging does not enable silent update application. The feed references the
versioned full `.nupkg` by its native filename and includes SHA-1, SHA-256, and
size. Full packages allow upgrades from earlier installed releases without a
delta chain. Old portable releases require manual replacement or running the
installer; they do not acquire an updater just by publishing a new release.

Only the full package and `releases.win-x64.json` are needed by this app's
`GithubSource`. Velopack's extra `assets.win-x64.json` upload-helper index and
legacy `RELEASES-win-x64` file remain in work staging and are not published.
The versioned Setup and stable Setup alias have the same bytes. All seven
published packages/feed files are covered by `SHA256SUMS.txt`; the manifest
does not hash itself. CI verifies the aliases, portable isolation, installer
product version, native package contents/version, and feed hashes before upload,
and the write-token release job repeats independent whitelist/hash/feed/package
checks without executing repository code.

For development, `-SkipInstaller` produces and verifies only the three ordinary
portable/source ZIPs; `-SkipPackage` skips both packaging forms. Default release
builds include the installer and native feed. Package generation and signatures
are separate: these releases are unsigned, so Windows may display publisher or
SmartScreen prompts. Publishing never replaces an existing published release.

Velopack references: [packaging](https://docs.velopack.io/packaging/overview),
[per-user Setup behavior](https://docs.velopack.io/packaging/installer),
[channels](https://docs.velopack.io/packaging/channels), and the pinned
[GithubSource feed implementation](https://github.com/velopack/velopack/blob/1.2.161/src/lib-csharp/Sources/GitBase.cs).

## Permissions and maintenance

Pull-request and build jobs use a read-only repository token, do not receive
release credentials, and check out source without persisted Git credentials.
The API-only manual tag-preparation job, release-publication job, and narrowly
scoped one-off v0.1.0 release and `portable-alias.yml` backfill jobs have
`contents: write`. These jobs never check out or execute repository scripts.
The workflows use GitHub's automatic token and require no personal access token
or extra secrets.
Repository policy must permit that job's release permission and enabled Actions.

All external actions are official GitHub actions pinned to full commit SHAs.
Dependabot checks those action pins monthly. Review and merge its updates after
CI passes. Update `global.json` and the local bootstrap SDK/checksum together
when changing the .NET SDK pin.
