# Native installer/update smoke test

Run on Windows x64 after the normal build has prepared the workspace-local .NET
SDK and pinned Velopack tool:

```powershell
.\scripts\build.ps1
.\tests\InstallerSmoke\Run.ps1
```

You can supply `-DotnetExe`, `-VpkExe`, and a fresh `-WorkDirectory`. The work
directory must be below this repository's `work/` directory. Logs and a
`smoke-results.json` report are retained there.

The test builds a console probe for versions 0.2.0 and 0.2.1, packages both, then
checks real installation, corrupted downloads, staging without application,
startup without automatic application, an approved SDK update/restart, downgrade
rejection, and uninstall. It uses the same pinned Velopack version and
`SetAutoApplyOnStartup(false)` setting as DiskPeek.

Each run uses a fresh application ID and an explicit installation path under
`work/`. It creates no shortcuts and does not use a normal DiskPeek installation.
One temporary uninstall registration is created under HKCU and removed by the
native uninstaller. The SDK's native updater may also leave a uniquely named
framework log under `%LOCALAPPDATA%\velopack`.

The restart probe uses a temporary Windows desktop for its own child processes,
without switching desktops or controlling input. This keeps the native progress
window away from the interactive desktop. Handles are closed after the test.

This local integration test is separate from the regular 33-test verification
suite. It does not exercise the WPF confirmation dialog or the public GitHub
feed. See [installer validation](../../docs/INSTALLER-VALIDATION.md) for coverage
and limits.
