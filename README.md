# WinStatusBar

A native Windows 11 status bar docked at the top of the screen, with a built-in AI assistant. Chat with several AIs (Claude, Codex, Gemini, Copilot and more) using the context of the active window, and let them automate your PC — files, Office, settings and apps — with verified, background-first actions.

![WinStatusBar](docs/winstatusbar-dark.png)

## Features

- **System monitoring** — CPU, RAM, GPU, network, battery and temperatures (when the PC exposes sensors), with detail panels on click.
- **AI quotas and tasks** — Codex and Claude Code usage limits and the task each agent is working on right now.
- **Multi-AI chat** — one command box for Claude, Codex, Gemini, Microsoft Copilot, Lenovo Qira, local models (Ollama) or your own API key, with the active window as context.
- **PC automation** — with your permission, the assistant works with apps, windows, files and folders, and checks each step.
- **Codex model picker** — the bar's AIs also show up in Codex's model selector.
- **Clipboard history and emoji picker**, shortcuts and a light/dark look that follows Windows.
- **Languages** — Portuguese (Brazil), English, Spanish, French, Japanese and Simplified Chinese.

## Install

**winget** (no SmartScreen prompt):

```
winget install SidneyJunior.WinStatusBar
```

**Installer:** download `WinStatusBar-Setup.exe` from the [latest release](https://github.com/smendesjunior/WinStatusBar/releases/latest) and run it. The 3-step wizard installs the bar for the current user only — no administrator rights needed. The installer is not code-signed yet, so Windows may show a SmartScreen prompt the first time (More info › Run anyway).

Requirements: Windows 10 (19041) or Windows 11, 64-bit. The Microsoft Edge WebView2 Runtime is installed automatically if it is missing. The .NET runtime and Windows App SDK are bundled.

Uninstall from Settings › Apps › Installed apps › WinStatusBar.

## Updates

The installed bar checks [GitHub Releases](https://github.com/smendesjunior/WinStatusBar/releases) every 15 minutes and updates itself silently: it downloads the new installer, verifies its size and SHA-256 against the release manifest, and reinstalls in the background. Settings › Updates shows the installed version, a **Check for updates** button and what changed in each version.

## Privacy

- No telemetry.
- The Claude Code hook collector only sends metadata (event, tool, file name, a short description) over a named pipe restricted to your Windows user — no conversations, file contents or credentials.
- API keys are stored in the Windows Credential Manager on this PC and are never synced.
- Logins for Claude Code, Codex, Gemini and Copilot stay with those tools; the bar only reads what it needs to show quotas and status.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows (x64).

```
dotnet test tests/WinStatusBar.Tests/WinStatusBar.Tests.csproj -c Release
powershell -File tools/build-release.ps1 -SandboxTest
```

`tools/build-release.ps1` runs the tests, publishes the app (self-contained), packs it into a single installer and, without `-SandboxTest`, publishes the release to GitHub.

| Project | Purpose |
|---|---|
| `src/WinStatusBar` | WinUI 3 app: the bar, panels, chat and settings |
| `src/WinStatusBar.Core` | Collectors and logic without UI (metrics, AI integrations, updates) |
| `src/WinStatusBar.Collector` | Minimal collector called by Claude Code hooks |
| `src/WinStatusBar.Sensors` | Temperature sensor reader |
| `src/WinStatusBar.Automation` | Desktop automation helper |
| `src/WinStatusBar.CodexIAs` | Local gateway that brings the bar's AIs to Codex |
| `src/WinStatusBar.Setup` | Per-user installer and uninstaller |

## Changelog

See [CHANGELOG.en.md](CHANGELOG.en.md).

---

© 2026 Sidney Junior. All rights reserved. Claude, Codex, ChatGPT, Gemini, Copilot, Qira, Ollama, Windows and other names belong to their respective owners; WinStatusBar is not affiliated with these companies.
