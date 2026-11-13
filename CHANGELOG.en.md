# WinStatusBar changelog

Notable changes in each released version. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/):
- **PATCH** for fixes;
- **MINOR** for new backward-compatible features;
- **MAJOR** for breaking changes.

The build number (UTC date and time) is what the updater compares; the version is for people.

## [1.13.1] - 2026-11-13 · build 20261113130704

### Fixed
- Automatic speed test: the unit now shows on both values, as in "D 96.7 Mb/s · U 99.3 Mb/s".

## [1.13.0] - 2026-11-13 · build 20261113130130

### Added
- Automatic speed test every 10 minutes: the network panel has an "Automatic test" row under the link rate, with D (download), U (upload), the ping of the last test and how long ago it ran. The test is light (about 16 MB at most). It pauses on metered networks (data plans, roaming or a USB-tethered phone), without internet, while the screen is off and during a manual test. It is turned on and off together with the speed test in Settings.
- Process search in the CPU panel: the box under "Apps" searches every process, including hidden and background ones, by executable name, description or window title. "End task" ends every process of that executable after a confirmation. Essential Windows processes show up in the search but can't be ended from it.

### Fixed
- App list: File Explorer and the shell windows are treated as protected again (no forced end), and the touch keyboard and emoji panel no longer show up as apps.

### Changed
- Win+V: the log records how long the bar takes to handle the shortcut and why the panel closed, to diagnose cases where it doesn't open.

## [1.12.3] - 2026-11-12 · build 20261112143828

### Fixed
- Pasting a clipboard item (Win+V) into the app you came from: the bar now hands focus back to that app before closing the panel and sends Ctrl+V only once the app is really in front. Before, the panel closed first, Windows sometimes activated another window, and Ctrl+V landed in the wrong place or nowhere. Apps running as administrator, which Windows protects, get the item ready for your own Ctrl+V.
- Win+V sometimes not opening the panel: when Windows refuses focus, the bar retries and, if that still fails, shows the panel on top of everything instead of behind the current window. The shortcut also recovers faster (within 5 s instead of 30 s) when Windows drops the keyboard hook on its own.
- Shadow around the chat's picker capsule and input box: it now follows the rounded corners, without the dark smudge and clipped corners.

## [1.12.2] - 2026-11-12 · build 20261112141822

### Fixed
- Chat model picker in "Gemini · API key" mode: models Google refuses for your key (retired ones such as Gemini 2.5, or ones your plan has no access to, such as Gemini 4 Argon and 4 Carbon) are now hidden, as they already were in Codex and the Office add-in. Before, this picker read Google's list directly and showed every model.
- A saved model choice that left the list falls back to the default model, and a Gemini CLI request refused by Google removes that model from the list right away.

## [1.12.1] - 2026-11-12 · build 20261112140401

### Changed
- Bar text is Semibold for both titles and values (network speed, GHz, GB, percentages). At Medium the small values still looked thin; the size is unchanged, so the bar doesn't get wider.

## [1.12.0] - 2026-11-12 · build 20261112133228

### Added
- Chat hover glow: buttons and pills light up softly in the Windows accent color while the mouse is over them; an active pill also shows a short accent line underneath. At rest they look exactly like native WinUI 3 controls. The glow is skipped when Windows animations are off or High Contrast is on.

### Changed
- Chat panel visual refinement in the WinUI 3 / Fluent style: every button is round, the AI, project and model selectors sit together in one capsule with thin dividers, the toolbar is regrouped (attach, Context, Web, Edit … options, dictation, voice, send), the send button uses the native accent style, and answers have a small avatar.
- The message box keeps its original shape and now has a light shadow outside it for depth over the acrylic.
- Chat menus (destination, attach, options, voice, continue, project, model) and the effort picker have concentric rounded corners.
- The chat follows a Windows theme change even while it stays open.

### Fixed
- Bar text is actually bolder now: the weight 450 from 1.11.0 was rejected by WinUI, so the bar stayed at Normal. It now uses the Medium weight.

## [1.11.0] - 2026-11-12 · build 20261112124208

### Added
- Only models that actually answer: the model check sends a short test request to every model of the key-based APIs (Gemini by API key, DeepSeek, Grok and custom APIs). Models the service refuses (retired or not included in your plan, such as Gemini 2.5 Pro, which Google no longer offers to new keys) are removed from the bar, Codex and the Office add-in lists, with the reason shown in Settings › Codex and models. Models that are only out of quota right now stay listed and are flagged.
- A regular request refused the same way also removes the model right away, and the error explains why.
- Automatic sync: the forwarder checks the models on its own every 6 hours (and 3 minutes after starting when the last check is older than 6 hours). The Office add-in reloads the list every 2 minutes.

### Fixed
- Claude in Codex with commands: programs installed while the bar was already running (for example Android's adb) were invisible to Claude, which spent minutes searching for or trying to install tools that were already there. Claude, Codex and Gemini CLI now get the current Windows PATH on every request.
- Google errors wrapped in a list (`[{"error": …}]`) showed only "HTTP 404"; the reason is shown now.

### Changed
- Slightly heavier bar text (Segoe UI Variable weight 450, between Normal and Medium).

## [1.10.4] - 2026-11-11 · build 20261112004236

### Fixed
- Windows sign-in: the bar draws itself before starting its readings (CPU, GPU, battery, temperatures, quotas, Codex, Claude Code and chats), which now start right after the first paint. At sign-in those readings competed with the bar for disk and CPU.
- Windows sign-in: the bar starts at above-normal priority and goes back to normal once its services are up, so it does not lose out to the other programs starting at the same time.

### Changed
- The startup log splits .NET and WinUI time and lists the libraries loaded before the bar appears.

## [1.10.3] - 2026-11-11 · build 20261112001012

### Fixed
- Windows sign-in: the bar shows up much sooner. It was already started together with Explorer, but it only drew itself after starting Codex, the model bridge, the Copilot, Gemini and Qira sessions, the clipboard history and the command bar shortcut; at sign-in that took about 30 s. The bar now appears first and those services start right after.
- The bar no longer loads, on every start, Task Manager, Settings and Microsoft Store icons that were never shown. At sign-in they waited for Windows to build its app list.

### Changed
- The bar log records how long each startup step took ("Abertura em … s").

## [1.10.2] - 2026-11-11 · build 20261111193117

### Fixed
- Codex: the context window declared for bar AIs goes from 10 million to 2 million tokens. It is still above every OpenAI model (switching models still does not compact), but a very long conversation is compacted by Codex again before the request goes over the gateway's 64 MB limit.
- Codex: a request too large for the gateway now shows "conversation too large… use /compact or start a new conversation" (in Portuguese) instead of "Invalid compressed request".

## [1.10.1] - 2026-11-11 · build 20261111191845

### Fixed
- Codex: switching to a bar AI (Gemini, Copilot, Gemini via API…) in the middle of a long conversation, for example after the previous model ran out of quota, produced a series of "usage limit reached" and "optimization interrupted" errors, whatever model was chosen. Bar AIs now declare a large context window to Codex (the gateway already decides what each AI receives), so Codex no longer compacts the conversation on the switch.
- Codex: if Codex still asks a bar AI to compact, the gateway answers in the new format Codex expects, with a summary of the conversation; the summary is read on the following requests and becomes a plain message if the conversation goes back to OpenAI.

### Changed
- Codex model picker: API-key models show the model name first and the source after it ("Gemini 3 Flash Preview · Google API"), so the name is no longer cut off.

## [1.10.0] - 2026-11-11 · build 20261111163307

### Added
- Choose for me: when no account or API is available, the question goes to the local AI (if turned on) and, last, to Lenovo Qira, instead of stopping at a notice.
- Choose for me: Lenovo Qira can be chosen when the request mentions Qira or depends on one of its features (with Qira installed and turned on).
- Choose for me: the choice now considers what each AI does, how it is billed (subscription, pay per use or no cost per request), the used quota of Claude and Codex, Qira credits, and whether there are attachments, Edit mode or web search.

### Changed
- Choose for me: only the start of a long request (2,000 characters) is sent to the model that makes the choice.
- Choose for me: the model lists of accounts and APIs are reused for 5 minutes, and one slow list no longer delays the others.
- A local AI conversation continues there with Choose for me selected.

### Fixed
- Choose for me: larger Gemini models were treated as lightweight because "mini" appears inside "gemini". The same error affected the model used for the task outline.
- Choose for me: an answer from the helper model wrapped in code fences or extra text was discarded and the default destination was used. It is now read and, if it is still unusable, the next helper model tries.

## [1.9.0] - 2026-11-11 · build 20261111162310

### Added
- Bar AIs in other programs on this PC: the local gateway (port 11435) now lists the AIs configured in the bar (`/codexias/modelos`) to programs on the same computer, such as the "IA Local" add-in for Word, Excel and PowerPoint, which now offers Gemini, DeepSeek, Grok, Lenovo Qira and custom APIs next to the local models. Keys stay in the Windows Credential Locker only.

### Changed
- The gateway also runs with the Codex link turned off when a program on the PC uses the bar's AIs (an `office.json` marker written by that program). Turning the Codex link off no longer stops it in that case.

## [1.8.0] - 2026-11-11 · build 20261111152910

### Added
- On-demand AI memory: picking an AI in the chat no longer opens anything. The hidden Gemini and Copilot page and Lenovo Qira open only when you send a question, close shortly after the answer and, when you switch AIs, the previous one's resources close right away (the local AI model is also unloaded on a switch).
- Settings › AIs and accounts › Memory: "Keep the AI open after the answer" (close right away, 1 minute, 5 or 15 minutes; default 1 minute).
- Network panel: the Mobile hotspot button is highlighted while it is on and shows how many devices are connected.
- Voice shortcuts (Bar chat): the field records the combination you press instead of requiring you to type it.
- Claude connectors: the list read by "Check" shows each connector with a colored state (connected, needs sign-in, failed), its name and origin; "Detect" adds every connected connector of the account to the always-allowed list in one go.

### Changed
- Lenovo Qira: the bar starts the Qira core on the first request (about 30 s to answer from fully closed, a few seconds for the next ones) and stops the whole of Qira after use, freeing 1.8 to 2.5 GB. Only a Qira opened by the bar is stopped; while the Lenovo app window is open it stays on.
- The AIs in Codex follow the same rule and the same wait for the hidden pages and for Qira.

### Fixed
- Opening the panel with Qira selected started (and sometimes restarted) the whole of Qira without any question.
- The hidden Gemini and Copilot pages stayed open for 15 minutes after each answer, and forever after closing the sign-in window or signing out.
- Codex quota: after the Codex app updated itself, the bar kept looking for the program in the old version folder and showed an English error until it was restarted. It now finds the new version by itself, and the error got a plain explanation.

## [1.7.0] - 2026-11-11 · build 20261111140800

### Changed
- Settings reorganized: 13 pages became 10. General, Bar and appearance, AIs and accounts, Quotas and tasks, Bar chat, Codex and models, Hardware, Network and battery, Clipboard and, in the footer, About and support.
- AIs and accounts (new page): sign-in, keys and connectors of each AI moved out of the middle of "AI and tasks" into one place, together with the own API ("Another AI by key").
- Bar and appearance: the shortcut buttons sit right below the bar items, and the color intensity sits next to the backdrop.
- Bar chat: every permission in one group (file edits, PC access and connectors); the voice shortcuts got titles.
- Quotas and tasks: what to track, quotas on the bar and the task on the bar in three groups; the Claude Code integration and the other local AI programs moved to "Advanced".
- Hardware: the detail panels open the page; reading frequency and sensor drivers are under "Advanced".
- About and support brings Updates, Diagnostics and About together. "What changed" is collapsed and only built when opened.
- Clearer names: "WinStatusBar notices", "Show quotas as", "Refresh quotas now", "API type", "Service address (URL)", "Remove key", "Install a driver from a file…", among others.
- Bar shortcuts open the right page: "Gemini · set up…" and "Set up" in the agent menu lead to AIs and accounts; "AI settings…" leads to Bar chat.
- Page, group and new option titles translated in the six languages.

### Removed
- Duplicate options: the battery switch (it lives in "Bar items"), the second data-folder button, the second Gemini sign-in button and the DeepSeek and Grok "Sign in/Sign out", which did the same as "Set up key…" and "Remove key". No saved preference changed.

### Fixed
- Updates: if replacing the files failed, the bar closed and reopened every minute. Now there are 3 attempts per build and then a 6-hour wait; the bar reopens even if a final step fails, and the update waits for an AI answer in progress.
- Preferences: an unreadable settings.json no longer silently resets everything (the backup and the OneDrive copy are tried before the defaults).
- Claude Code: on profiles with a space in the path, the bar duplicated its own hooks in Claude's settings.json every 10 minutes.
- Chat: pressing Enter twice no longer duplicates the question; a Claude error is no longer saved as the answer; API answers are no longer cut short on services such as Azure and OpenRouter; the view no longer jumps to the end after you scroll up.
- Bar: Alt+F4 no longer leaves the bar on screen with dead panels; old windows are released from memory on every monitor change.
- Executor: deleting or overwriting on a network folder or removable drive is refused (there is no Recycle Bin there); "end process" refuses the Qira parts and the bar itself.
- AIs in Codex: the gateway is checked every minute and restarted if it stops; Continue via the API retries after a transient network failure.
- "Refresh quotas now" shows that it is checking; the interval and the way quotas are shown stay available even with Codex and Claude turned off.
- API keys masked in the bar's log.

## [1.6.1] - 2026-11-11 · build 20261111033512

### Fixed
- Codex quota menu: the account block notice moved to the footer, next to the stopped conversations, in a card set apart from the quotas, and now takes two lines (reason; when it comes back and where that came from).
- Quota menu: a scroll bar showed up even with room left on screen (the menu's default height limit cut it at about 760 points); the menu now grows to the bottom of the screen and only scrolls when it does not fit.

## [1.6.0] - 2026-11-11 · build 20261111031856

### Added
- Bar AIs in Codex: Gemini and Copilot through the account, API-key models, local models and Claude without the commands option now work on files. They ask for each command or file to write, and Codex itself runs it, with the conversation's permissions and showing every step.
- Switching AIs mid-conversation in Codex: the model that takes over gets the original request, the earlier commands and results (from any model), a notice that the previous answer was interrupted and the project rules (AGENTS.md).
- Settings › Models: an option to turn Continue via the API on or off.

### Changed
- Codex quota menu: the conversations stopped by the quota and Continue via the API moved to the footer, across both columns (summary and button on the left, progress on the right).

### Fixed
- Compacting a Codex conversation with a bar AI no longer wipes the work done: a summary with the messages, commands and results is kept instead of only the last 6 messages.
- "OpenAI — my API" in a conversation that started on an account model: the reasoning encrypted by the account, which the API rejects, is left out.
- "Use this reset", "Configure" and "Sign out…" (quota menu) and "Rename" (recent folders) opened an invisible dialog that blocked clicks; the question now shows inside the menu itself.

## [1.5.1] - 2026-11-10 · build 20261111011834

### Fixed
- Codex › Continue via the API: the final spend no longer shows US$ 0.00 when the OpenAI dashboard has not reported the cost yet. The dashboard shows the requests before the cost and does not update the balance right away; the card now says the cost has not been reported yet, uses the day's cost when the balance has not moved and checks again 5, 15, 60 and 180 minutes after the end.

## [1.5.0] - 2026-11-10 · build 20261111005816

### Added
- Codex › Continue via the API (quota menu): conversations stopped for lack of quota continue through your OpenAI API key, in the same conversation and with the same model and level, with live spend and a Stop button.
- Settings › Models: Claude in the Codex model picker can run commands and edit files (off by default); during long steps, Codex gets the notice that Claude is still working.

### Fixed
- When the Codex account is blocked, the quota shows the reason and when it returns, and the stopped conversation no longer turns into "Unknown state".
- Closing the Codex AI component also ends Claude Code and its commands.
- The thin line at the bottom of the bar no longer flickers over maximized Claude, ChatGPT and Edge windows: it shows whenever a maximized window is below the bar.

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
