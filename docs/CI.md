# Build and release automation

The `Build and release` GitHub Actions workflow runs on pull requests to `main`,
pushes to `main`, version-tag pushes, and manual **Run workflow** requests.
Pushes to `main` and manual runs build downloadable artifacts; they do not
create a GitHub release.

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
both versioned ZIP packages, and matching entries in `SHA256SUMS.txt`. Skipped
environment-dependent tests remain visible in the report and run summary.

After a successful run, open **Actions → Build and release → the run → Artifacts**.
The `DiskPeek-<version>-win-x64-<run>-<attempt>` artifact contains:

- `DiskPeek-<version>-win-x64.zip`: the self-contained portable Windows app.
- `DiskPeek-<version>-source.zip`: the corresponding source, including CI files.
- `SHA256SUMS.txt`: SHA-256 checksums for the packages.

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
2. Create and push an annotated tag matching that version exactly, such as
   `v0.1.0` or `v0.2.0-rc.1`.
3. Wait for the Windows build and verification to pass. The release job checks
   the downloaded artifact's integrity and the package checksums, creates a draft
   GitHub release, attaches both ZIPs and `SHA256SUMS.txt`, then publishes it.

A mismatched or malformed `v*` tag fails before building. Versions containing a
prerelease suffix are published as GitHub prereleases. Manual runs always stop
after artifact creation, even when launched against a tag. No release is made
simply by pushing the repository for the first time.

The release job can resume an existing draft after an interrupted upload. It
refuses to overwrite an already published release; use a new version/tag for
changed binaries. Releases keep their downloadable assets independently of the
30-day Actions-artifact retention window.

## Permissions and maintenance

Pull-request and build jobs use a read-only repository token, do not receive
release credentials, and check out source without persisted Git credentials.
Only the tag-triggered release job has `contents: write`; it downloads verified
artifacts and does not check out or execute repository scripts. The workflow uses
GitHub's automatic token and requires no personal access token or extra secrets.
Repository policy must permit that job's release permission and enabled Actions.

All external actions are official GitHub actions pinned to full commit SHAs.
Dependabot checks those action pins monthly. Review and merge its updates after
CI passes. Update `global.json` and the local bootstrap SDK/checksum together
when changing the .NET SDK pin.
