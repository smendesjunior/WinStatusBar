# WinStatusBar changelog

Notable changes in each released version. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/):
- **PATCH** for fixes;
- **MINOR** for new backward-compatible features;
- **MAJOR** for breaking changes.

The build number (UTC date and time) is what the updater compares; the version is for people.

## [1.4.1] - 2026-11-09 · build 20261109103623

### Fixed
- Auto-update: a connection drop during the download now explains that the connection to GitHub dropped, instead of showing a raw technical message.
- Auto-update: an installer that was already downloaded and verified is reused. After 3 failures in a row for the same version, automatic checks wait 6 hours (the "Check for updates" button still tries right away). Before, a repeating failure re-downloaded about 270 MB every 15 minutes.
- Networks behind a proxy that requires the Windows login: updates and the Microsoft Edge WebView2 download now send the account's credentials.
- Installer: when installation fails, step 3 explains why in plain language (access denied, file in use, disk full) and only says nothing was changed when the old files were still in place.

### Changed
- Publishing: the secret scan also checks commits that were not pushed yet and internal names, and an already published version is refused before anything changes.

## [1.4.0] - 2026-11-09 · build 20261109093522

### Added
- 3-step setup wizard (About → Installing → Done) when the installer is opened with a double-click. Automatic updates still run with no window at all.
- Settings › Updates (new tab): installed version, a "Check for updates" button with live progress (checking, downloading, installing, or the reason it failed) and what changed in every version.
- winget package with no SmartScreen prompt: `winget install winstatusbar` (once Microsoft approves it).

### Changed
- Automatic updates now come from GitHub Releases (smendesjunior/WinStatusBar), every 15 minutes, silently and without administrator rights. The downloaded installer is verified (size and SHA-256) before it runs.
- Last version distributed through the previous network share and OneDrive folder. PCs that receive it there switch to GitHub updates.
- About: the updates row moved to the new Updates tab.
- Apps & features: publisher "Sidney Junior" and a link to the project page.

## [1.0.0 – 1.3.2] - 2026-11-08 to 2026-11-09

The first releases were documented in Portuguese only. See [CHANGELOG.md](CHANGELOG.md).
