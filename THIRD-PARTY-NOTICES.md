# DiskPeek dependency notices

DiskPeek's original source is licensed under AGPL-3.0-or-later; see LICENSE.

The app uses Microsoft .NET 10 and Windows Presentation Foundation (WPF).
The self-contained distribution includes Microsoft runtime files under their
respective licenses. The official Microsoft license and third-party notices are
included as `DOTNET-RUNTIME-LICENSE.txt` / `DOTNET-RUNTIME-NOTICES.txt`
(copied from the published runtime pack) and `WPF-LICENSE.txt` /
`WPF-NOTICES.txt` (from the official WPF repository) in the portable package.
This does not change the licenses of the Microsoft components.

There are no additional third-party NuGet library dependencies in DiskPeek's
source projects. Windows system libraries are provided by the operating system.

DiskPeek is an independent project. No WizTree or WinDirStat source, branding,
icons, or proprietary scanning libraries are included.
