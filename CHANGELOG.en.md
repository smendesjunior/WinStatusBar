# WinStatusBar changelog

Notable changes in each released version. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/):
- **PATCH** for fixes;
- **MINOR** for new backward-compatible features;
- **MAJOR** for breaking changes.

The build number (UTC date and time) is what the updater compares; the version is for people.

## [1.4.8] - 2026-11-10 · build 20261110135241

### Fixed
- Memory: every time Settings was opened and closed, the whole window stayed in memory, about 36 MB each time, and kept refreshing on every Gemini quota read. WinUI does not release closed windows, so the same window is now reused: closing just hides it, and while hidden it does no reads. Measured: 4 openings kept 146 MB; now the first one costs about 18 MB once and the next ones add nothing.
- Memory: the AI logos (Gemini, Copilot, OpenAI and others) were drawn at 1898 × 1898 pixels, 14 MB each, to be shown at about 20 pixels. They are now drawn at the size they are shown: the quota icons alone took up to 58 MB. In Task Manager, the bar now starts at about 233 MB instead of 253 MB.
- Clipboard: the image from a screenshot copy is released right after it is converted, instead of waiting for the next memory collection.

## [1.4.7] - 2026-11-10 · build 20261110094036

### Fixed
- Memory: in 1.4.6, returning to Windows the memory .NET reserves at startup (1 minute after the bar opens) never happened, because its timer was discarded before it fired; only the release when the screen turns off worked. Both work now (measured right after startup: from 80 to 26 MB reserved).

## [1.4.6] - 2026-11-10 · build 20261110093029

### Fixed
- CPU use with the screen off or the session locked: the bar kept working for nobody. It read the sensors every second, checked the ChatGPT and Claude windows, opened hidden browsers to read quotas (every 5 to 6 minutes), captured the screen for the adaptive background and animated the agent module. All of this now stops when the session's screen turns off or the session is locked, and resumes as soon as the screen turns back on (measured with the screen off: from 3.9% to 0.5% of a CPU core).
- CPU use while an AI is working: the outline glow, the Claude star, the spinning logos and the task ring now run on a single clock of about 32 frames per second. Before, these animations made the bar redraw at the screen's refresh rate (165 times per second on this PC). It looks the same (measured while an AI was working: the bar went from 15.6% to 4.5% of a core, and screen drawing from 11.1% to 1.8%).
- Graphics card: usage and temperature now come straight from the Windows graphics kernel, the same source as Task Manager. Before, each usage read went through about 900 counters (3 ms of CPU every 2 seconds), and the NVIDIA temperature came from nvidia-smi.exe, started every 10 seconds (44 ms of CPU each time, and it could wake the dedicated card just for that). If the dedicated card is powered off to save energy, its temperature stays hidden until the card is used again.
- CPU panel: the process, thread and handle counts now come from a single Windows call. The counters used before went through every process and thread on the PC every second, using about 1% of a core just to show three numbers.
- Task steps: each line of the conversation is now parsed only once. Before, the bar kept up to 2 million characters of each conversation in memory and parsed all of it again on every change, several times a minute while the AI works.
- Conversations in the ChatGPT and Claude apps: the app window is checked every 2.5 seconds only while the app is in use or has a conversation in progress; otherwise, every 10 seconds. Each check also makes the checked app do work (measured: an idle ChatGPT went from 0.3% to 2.5% of a core just because of them).
- Local AI: with Ollama closed, the bar looks for the server every 10 seconds instead of listing every process on the PC every 2 seconds.
- Temperature from LibreHardwareMonitor or OpenHardwareMonitor: when they are not installed, the bar stops asking every 10 seconds (each attempt ended in an error in the WMI service).
- Memory: the bar returns to Windows the memory .NET reserves at startup and when the screen turns off (measured: from 100 to 21 MB reserved), and now uses the Segment Heap, the Windows memory manager used by Edge and Chrome. In Task Manager, the bar went from about 356 to about 260 MB.

## [1.4.5] - 2026-11-09 · build 20261110024050

### Fixed
- Memory and disk with long Claude Code or Codex sessions: on every change to the conversation, the bar re-read the whole conversation file (8.8 MB in a long session, several times a minute) to follow the task's steps. The large lines piled up in memory (about 280 MB extra) and the disk was read all the time. It now reads only what was added since the last time (measured: disk reads from 184 to 10 KB/s, and the bar at about 350 MB instead of 600 to 660 MB).
- CPU and graphics card use while an AI is working: the colored glow around the agent module was re-blurred on every screen frame (165 times per second on this PC, on both bars) and took 18 to 35% of a CPU core and 8% of the graphics card. The blurred outline is now drawn once and the colors move behind it 30 times per second. It looks the same and uses about half the CPU and graphics card (measured: from 17.9% to 8.3% of a core and from 8.4% to 3.6% of the graphics card).
- Local AI: when the graphics card makes a calculation error (a CUDA error such as "illegal memory access" or "misaligned address", common when the card is overclocked beyond what it can handle), the message now says so and suggests lowering the overclock. Before, it blamed missing memory. If Ollama is reloading the model after a crash, the bar asks you to try again in a few seconds instead of saying the service is unstable and "not your PC's fault".
- Local models that think (such as Qwen 3.5) in Codex: the picker only showed "High", with nothing to choose, because Codex does not show the "no reasoning" level. It now shows **Low** (no reasoning, faster) and **High** (thinks before answering), and Low really turns reasoning off. Models already in the picker get both levels at the next check of the bar's AIs in Codex.

## [1.4.4] - 2026-11-09 · build 20261110010828

### Fixed
- Memory use: with everything on, the bar used about 1.7 GB, of which 1.3 GB were hidden WebView2 browsers kept open all the time (Task Manager lists them as "Microsoft Edge"). These browsers now open only when they are used, and the bar stays around 350 MB between reads.
  - Gemini quotas, Microsoft credits and the OpenAI dashboard: the browser opens for each read (a few seconds, at the quota interval) and closes right after. Sign-ins are kept. While the quota menu is open, Gemini quotas still refresh every 2 seconds.
  - Closing the OpenAI dashboard window now really closes it instead of just hiding it.
  - Copilot and Gemini conversations through their websites: the page closes after 15 idle minutes. The next question opens a new one, with the same sign-in and the bar's conversation history.

## [1.4.3] - 2026-11-09 · build 20261109201912

### Fixed
- Closing the bar (tray menu, update, uninstall or `tools\stop-bar.ps1`) sometimes did not end the program: the bar disappeared from the screen, but WinStatusBar kept running hidden, without its services, until it was killed (during updates, the installer did that).
  - Cause: WinUI only ends when the program's last window closes. The Gemini quota and Microsoft credits readers use hidden WebView2 windows, and their timers kept running after the exit request and recreated those windows, so the last window never closed.
  - Those timers now stop as soon as exit starts, and the program ends on its own right after removing the bar, the tray icon and the Codex bridge, without waiting for WinUI to tear down. That teardown could crash the program while an AI was answering, and Windows Error Reporting then held the exit for 10 to 25 seconds.
  - Safety limit: if exit does not finish within 3 seconds (for example, while the bar is busy), the bar itself releases the space reserved at the top of the screen and ends. The log (`winstatusbar.log`) records which step it was in.

## [1.4.2] - 2026-11-09 · build 20261109163414

### Changed
- The bar's AIs in Codex: the automatic link now happens only where the bar was already installed (on update, when the PC has Codex and the link was never made). On a fresh install the link stays off until you click "Link to Codex" in Settings › Models.

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
