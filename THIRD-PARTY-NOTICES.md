# DiskPeek dependency notices

DiskPeek's original source is licensed under AGPL-3.0-or-later; see LICENSE.

The app uses Microsoft .NET 10 and Windows Presentation Foundation (WPF).
The self-contained distribution includes Microsoft runtime files under their
respective licenses. The official Microsoft license and third-party notices are
included as `DOTNET-RUNTIME-LICENSE.txt` / `DOTNET-RUNTIME-NOTICES.txt`
(copied from the published runtime pack) and `WPF-LICENSE.txt` /
`WPF-NOTICES.txt` (from the official WPF repository) in the portable package.
This does not change the licenses of the Microsoft components.

The Windows installer and application updater use **Velopack 1.2.161**, licensed
under the MIT license. Copyright © 2021 Caelan Sayler and © 2024 Velopack Ltd.
The complete license is included in `docs/licenses/VELOPACK-LICENSE.txt`.
The application library and packaging tool are pinned to the same version.
See [Velopack source](https://github.com/velopack/velopack/tree/1.2.161).
This dependency does not change DiskPeek's AGPL-3.0-or-later license.
The upstream packaging-tool notices are preserved in
`docs/licenses/VELOPACK-TOOLS-NOTICES.md` for build provenance. Those vendor
packaging executables are not included in DiskPeek's installed application.

Windows system libraries are provided by the operating system.

DiskPeek is an independent project. No WizTree or WinDirStat source, branding,
icons, or proprietary scanning libraries are included.
