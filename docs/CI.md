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
both versioned ZIP packages, the stable portable alias, and matching entries in
`SHA256SUMS.txt`. CI also checks that the alias is byte-identical to the versioned
portable ZIP. Skipped environment-dependent tests remain visible in the report
and run summary.

After a successful run, open **Actions → Build and release → the run → Artifacts**.
The `DiskPeek-<version>-win-x64-<run>-<attempt>` artifact contains:

- `DiskPeek-<version>-win-x64.zip`: the self-contained portable Windows app.
- `DiskPeek-<version>-source.zip`: the corresponding source, including CI files.
- `DiskPeek-portable-win-x64.zip`: a byte-identical copy of the versioned portable ZIP.
- `SHA256SUMS.txt`: SHA-256 checksums for all three ZIPs in new builds.

Extract the Actions artifact, then extract the portable app ZIP before launching
`DiskPeek.exe`. The separate `verification-<run>-<attempt>` artifact retains the
test report, including on failed builds when the runner produced it. Artifacts
are retained for 30 days, subject to repository policy. A build failing before
tests start has no test report; this is reported as a missing diagnostics file.

Hosted CI does not run the interactive WPF benchmark or accept UAC prompts.
Live elevated scans, GUI interaction, and external storage/provider scenarios
retain the verification limits described in `VALIDATION.md`.

## Publishing a release

1. Set `Version` in `Directory.Build.props`, commit the change, and merge to `main`.
2. Open **Actions → Build and release → Run workflow**, select `main`, and enter
   the matching `release_tag`, such as `v0.1.1` or `v0.2.0-rc.1`. Alternatively,
   create and push an annotated tag matching the project version.
3. Wait for the Windows build and verification to pass. The release job checks
   the downloaded artifact's integrity and the package checksums, creates a draft
   GitHub release, attaches both versioned ZIPs, the stable portable alias, and
   `SHA256SUMS.txt`, then publishes it.

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
The narrowly
scoped `release-v0.1.1.yml` helper invokes this same path with both inputs; it has
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

## Permissions and maintenance

Pull-request and build jobs use a read-only repository token, do not receive
release credentials, and check out source without persisted Git credentials.
The API-only manual tag-preparation job, release-publication job, and narrowly
scoped one-off v0.1.0 release and `portable-alias.yml` backfill jobs have
`contents: write`. These jobs never check out or execute repository scripts.
The workflows use
GitHub's automatic token and require no personal access token or extra secrets.
Repository policy must permit that job's release permission and enabled Actions.

All external actions are official GitHub actions pinned to full commit SHAs.
Dependabot checks those action pins monthly. Review and merge its updates after
CI passes. Update `global.json` and the local bootstrap SDK/checksum together
when changing the .NET SDK pin.
