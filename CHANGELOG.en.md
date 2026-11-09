# WinStatusBar changelog

Notable changes in each released version. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/):
- **PATCH** for fixes;
- **MINOR** for new backward-compatible features;
- **MAJOR** for breaking changes.

The build number (UTC date and time) is what the updater compares; the version is for people.

## [1.4.0] - 2026-11-09 · build BUILD

### Added
- 3-step setup wizard (About → Installing → Done) when the installer is opened with a double-click. Automatic updates still run with no window at all.
- Settings › Updates (new tab): installed version, a "Check for updates" button with live progress (checking, downloading, installing, or the reason it failed) and what changed in every version.
- winget package with no SmartScreen prompt: `winget install SidneyJunior.WinStatusBar` (once Microsoft approves it).

### Changed
- Automatic updates now come from GitHub Releases (smendesjunior/WinStatusBar), every 15 minutes, silently and without administrator rights. The downloaded installer is verified (size and SHA-256) before it runs.
- Last version distributed through the previous network share and OneDrive folder. PCs that receive it there switch to GitHub updates.
- About: the updates row moved to the new Updates tab.
- Apps & features: publisher "Sidney Junior" and a link to the project page.

## [1.0.0 – 1.3.2] - 2026-11-08 to 2026-11-09

The first releases were documented in Portuguese only. See [CHANGELOG.md](CHANGELOG.md).
