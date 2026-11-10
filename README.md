<h1 align="center">WinStatusBar</h1>

<p align="center">
  <b>Your AIs, your PC, one bar.</b><br>
  A native Windows 11 command bar that talks to Claude, Codex, Gemini, Copilot, Qira and local models,<br>
  runs real automations on your PC straight from the bar, plugs every AI into Codex, and keeps your quotas and hardware in sight.
</p>

<p align="center">
  <a href="https://github.com/smendesjunior/WinStatusBar/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/smendesjunior/WinStatusBar?label=download&color=2ea44f"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="WinUI 3" src="https://img.shields.io/badge/UI-WinUI%203-4B9CD3">
</p>

<p align="center">
  <img src="docs/screenshots/reel.webp" alt="Animated tour of WinStatusBar: the agent glowing while Claude Code works, the agent menu, Codex and Claude quotas, a request typed in the Ask box, the live CPU panel, the bar over Codex and File Explorer, and light mode (personal items blurred)">
</p>

<p align="center">
  <img src="docs/screenshots/bar-left.png" alt="Left side of the bar: AI agent, Codex, Claude and Gemini quotas, current task and the Ask your AI box">
  <img src="docs/screenshots/bar-right.png" alt="Right side of the bar: network, CPU, RAM, GPU, battery and shortcuts">
</p>

<p align="center"><sub>Docked at the top of the screen like a native Windows AppBar. The animation and screenshots show the Portuguese (Brazil) interface; English, Spanish, French, Japanese and Simplified Chinese are included.</sub></p>

## Why WinStatusBar

| | |
|---|---|
| ⚡ **Commands straight from the bar** | Type what you need — "rename these files", "fill this Excel sheet", "install this app" — and the bar's executor does it on your PC with built-in, self-checking actions. |
| 🪙 **Fewer tokens per task** | Ready-made actions return short, verified results, and the bar remembers the steps that already worked on your PC, so the AI plans less and spends less. Context, Web and Edit start the way you set them. |
| 🔌 **Every AI inside Codex** | Claude Code, Gemini, Copilot, Lenovo Qira, your API keys and local models show up in Codex's model picker through a gateway that runs only on your PC. |
| 🔄 **Models always in sync** | One click re-reads every configured AI and updates the bar's chat and Codex's picker at once; new local Ollama models appear on their own. |
| 📊 **Quotas and agents at a glance** | 5-hour and weekly limits, API spending and the task each agent is running right now. |
| 🖥️ **Hardware panels** | CPU, GPU, RAM, network and battery panels with live charts, temperatures and End task. |
| 🛡️ **Silent, safe updates** | Updates install by themselves from GitHub Releases, verified by SHA-256, with no prompts and no administrator rights. |

## Feels like part of Windows

The bar blends into whatever is below it. Over a maximized window it takes on that window's title bar, so the app and the bar read as one surface; over the wallpaper it turns transparent, and each part picks light or dark text for contrast.

<table>
  <tr>
    <td width="50%" align="center"><b>Over Codex</b><br><img src="docs/screenshots/integration-codex.png" alt="WinStatusBar above a maximized Codex window, matching its title bar (personal items blurred)"></td>
    <td width="50%" align="center"><b>Over Claude</b><br><img src="docs/screenshots/integration-claude.png" alt="WinStatusBar above a maximized Claude window, matching its title bar (personal items blurred)"></td>
  </tr>
  <tr>
    <td width="50%" align="center"><b>Over File Explorer</b><br><img src="docs/screenshots/integration-explorer.png" alt="WinStatusBar above a maximized File Explorer window, matching its Mica tab bar (personal folders blurred)"></td>
    <td width="50%" align="center"><b>Light mode · desktop with the CPU panel open</b><br><img src="docs/screenshots/integration-desktop-light.png" alt="WinStatusBar in light mode, transparent over the wallpaper, with the CPU detail panel open"></td>
  </tr>
</table>

## Install

```
winget install winstatusbar
```

Or download **`WinStatusBar-Setup.exe`** from the [latest release](https://github.com/smendesjunior/WinStatusBar/releases/latest) and run it: a 3-step wizard installs the bar for the current user, with no administrator rights. The installer is not code-signed yet, so Windows may show a SmartScreen prompt the first time (More info › Run anyway). winget installs don't show it.

<p align="center"><img src="docs/screenshots/setup.png" width="480" alt="Setup wizard, step 1: what WinStatusBar is, what the installation does and the Install button"></p>

**Requirements:** Windows 10 (19041) or Windows 11, 64-bit. The .NET runtime and Windows App SDK are bundled; the Microsoft Edge WebView2 Runtime is installed automatically if it is missing.

---

## Ask your AI from anywhere

One box at the center of the bar talks to **Claude Code, Codex, Gemini, Microsoft Copilot, Lenovo Qira, local models (Ollama)** or your own API key. The active window is attached as context, so you can ask about what is on screen without copying anything. Switch AI, model or reasoning level mid-conversation without losing the thread.

<p align="center"><img src="docs/screenshots/chat.png" width="760" alt="Chat panel with the AI selector, project, model, context chip and the Context, Web and Edit toggles"></p>

## Run commands on your PC, right from the bar

Turn on **Edit** and ask. The bar's executor works the same way for every AI, with ready-made actions that check their own result:

- **Files and folders** — create, rename in bulk, move, convert, organize.
- **Office** — Word, Excel and PowerPoint through COM, without clicking around.
- **Windows and apps** — open, focus, type, press controls by name, read the active window.
- **System** — settings, services, registry, printing, downloads, app installs with winget and PowerShell when needed.

Every step is verified before the next one, results come back as short text instead of long dumps, and paths that already worked on your PC are remembered and reused. The AI plans less, finishes in fewer rounds and spends fewer tokens. The executor keeps your Windows permissions; anything that needs administrator rights still asks through UAC.

## Codex, supercharged

- **All your AIs in Codex's model picker** — Claude Code (with your account), Gemini and Copilot (with their site modes), Lenovo Qira, your API keys and local models, served by a local gateway that only listens on your PC. It keeps working with the bar closed and starts with Windows.
- **Set up by itself** — the first time the bar finds Codex it links everything, with a backup of `config.toml`. One click undoes it.
- **One-click model sync** — **Check and update models** re-reads every configured AI and updates the bar and Codex together, flags OpenAI models that look obsolete (they only leave with your confirmation) and brings new Ollama models in.
- **Fix AI problems** — one button checks and repairs the gateway, Codex's config, Ollama and Qira, and tells you exactly what still needs a login.
- **Follow Codex from the bar** — current task, progress and 5-hour/weekly quotas without opening Codex.
- **Continue via the API** — Codex conversations stopped by quota continue through your OpenAI API key.

## Every AI at a glance

See which agent is working, what it is doing right now and how much of each plan is left — Codex, Claude Code, Gemini, Copilot, Qira and local AI side by side. The bar can follow the busiest agent automatically.

<p align="center"><img src="docs/screenshots/agent.png" width="560" alt="Agent menu with Codex, Claude Code, Gemini, Copilot, Lenovo Qira and local AI, each with status and quota rings"></p>

## Quotas without opening a browser

Five-hour and weekly windows, reset times, per-product usage and, for the OpenAI API, tokens, spending and requests.

<table>
  <tr>
    <td width="65%"><img src="docs/screenshots/codex-quotas.png" alt="Codex quotas with 5-hour and weekly windows and OpenAI API usage charts"></td>
    <td width="35%"><img src="docs/screenshots/claude-quotas.png" alt="Claude quotas with 5-hour and weekly windows and usage per product"></td>
  </tr>
</table>

## Hardware at a glance

Click any metric for a live panel in the style of Task Manager: usage history, temperatures (when the PC exposes sensors), top apps with **End task**, memory composition, GPU engines, Wi-Fi, Bluetooth, hotspot and a speed test.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/cpu.png" alt="CPU panel with usage chart, temperature, specs and top apps"></td>
    <td width="50%"><img src="docs/screenshots/network.png" alt="Network panel with Wi-Fi, Bluetooth, hotspot, speed test and traffic chart (network names and addresses blurred)"></td>
  </tr>
</table>

## And more

- **Clipboard history and emoji picker**, Windows shortcuts and a light/dark look that follows Windows.
- **Settings › Updates** shows the installed version, a **Check for updates** button and what changed in each version.

## Updates

The installed bar checks [GitHub Releases](https://github.com/smendesjunior/WinStatusBar/releases) every 15 minutes and updates itself silently: it downloads the new installer, verifies its size and SHA-256 against the release manifest and reinstalls in the background — no prompts, no administrator rights.

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
| `src/WinStatusBar.Setup` | Per-user installer and uninstaller with the 3-step wizard |

## Changelog

See [CHANGELOG.en.md](CHANGELOG.en.md).

---

<sub>© 2026 Sidney Junior. All rights reserved. Claude, Codex, ChatGPT, Gemini, Copilot, Qira, Ollama, Windows and other names belong to their respective owners; WinStatusBar is not affiliated with these companies.</sub>
