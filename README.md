# Window Top Tool

## Overview

**Window Top Tool** is a lightweight Windows utility for pinning windows
always-on-top, with click-through, mini-window, picture-in-picture, edge
auto-hide, opacity control, and state persistence. It runs from the system
tray, and with the PPC Central Processing System
<html><!- [PPC Central Processing System](../main/README.md) -!> </html> for centralized
application management and logging.

- **Version:** 0.0.9
- **Target:** .NET 10 (`net10.0-windows10.0.26100.0`), WinUI 3 UI with a native (`Shell_NotifyIcon`) system tray and Win32 window integration
- **License:** GPL-3.0 (see `LICENSE`)

## Key Features

### 📌 Window Pinning
Keep any window always on top. Operate via the tray menu, or
the window list in the settings dialog.

### 🖱️ Click-through
When a pinned window is inactive, a single click passes through to the
windows below; a **double-click** activates the pinned window, after which
all actions apply to it normally.

### 🗜️ Mini Window
Shrink a pinned window into a mini preview to save screen space.

### 🖼️ Picture-in-Picture
Drag a window to the right screen edge to auto-hide it as a picture-in-picture
overlay; click the overlay to restore it.

### ↔️ Edge Auto-Hide
Detects the gesture of dragging a window to a screen edge and automatically
triggers picture-in-picture mode.

### 👁️ Opacity Control
Pinned windows support custom opacity, adjustable in general settings
(20%–100%).

### 💾 State Persistence
Automatically saves the pinned window list and restores it after restart.

### 🟢 System Tray
A persistent tray icon with a right-click menu for quick actions; the icon
shows the current pinned window count. Clicking the window's close (X) button
prompts whether to **Exit** or **Minimize to tray**, with a **Remember my
choice** option that persists the decision so future closes skip the prompt.
The tray **Exit** menu item always quits.

### 🔒 Single Instance
Only one copy runs per user session. Launching a second instance brings the
already-running window to the foreground instead of starting another process.

### 🔐 Administrator Restart
From **General Settings → Save Settings**, the "Restart as administrator"
button elevates the process via UAC and relaunches the app with full
privileges — required to pin windows owned by elevated processes. The
button is automatically hidden when the app is already running as admin.

## System Requirements

| Requirement | Detail |
|-------------|--------|
| **Operating System** | Windows 10 (1809+) or Windows 11. x86, x64 and ARM64 builds are supported. Uses WinUI 3, a native `Shell_NotifyIcon` tray, `SetWindowPos`, and low-level mouse hooks (`SetWindowsHookEx`). |
| **Runtime** | .NET 10 Desktop Runtime. The Windows App SDK runtime is configured as self-contained and is included with the application. |
| **Permissions** | **Administrator recommended.** Required to pin windows owned by elevated processes. Without admin, higher-privilege windows may be unpinnable (the app prompts on launch). |
| **PPC** | PPC server v0.0.8 running locally on `127.0.0.1:9527`. Required from v0.0.5 onward. |
| **Disk** | Larger than the previous framework-dependent build because the Windows App SDK runtime is bundled. |

## Installation

### Run from source

```powershell
dotnet run -p:Platform=x64 -p:WindowsAppSDKSelfContained=true
```

Use the .NET 10 SDK. Configuration and logs are stored under
`%AppData%\wang.station\app\WindowTopTool\`.

### Build

Build for the current machine's architecture — the generic command, no
architecture flags to get wrong:

```powershell
dotnet build -c Release -p:WindowsAppSDKSelfContained=true
```

The executable is produced under
`bin\Release\net10.0-windows10.0.26100.0\WindowTopTool.exe`.

To target a specific architecture instead, add the matching platform and
Runtime Identifier:

```powershell
# 32-bit (x86)
dotnet build -c Release -r win-x86 --self-contained true -p:Platform=x86 -p:WindowsAppSDKSelfContained=true -o bin\Release\net10.0-windows\win-x86\publish

# 64-bit (x64)
dotnet build -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -o bin\Release\net10.0-windows\win-x64\publish
```

The architecture-specific executables are:

- `bin\Release\net10.0-windows\win-x86\publish\WindowTopTool.exe`
- `bin\Release\net10.0-windows\win-x64\publish\WindowTopTool.exe`

Run these commands from the `TOP_APP` directory.

### System Tray

- **Left-click** the tray icon to open the settings dialog.
- **Right-click** for quick actions: pin active window, unpin all, settings, exit.
- The tray icon badge reflects the current pinned window count.

### Settings Window

- WinUI 3 navigation separates window management, general settings, and about;
  each page fills and reflows with the window size.
- Double-click a window to pin or unpin it; filter the list by title or process.
- Adjust opacity, edge threshold, auto-save interval, PiP size, notification
  preferences, and language. Changes are saved through the existing config store.
- The General page keeps a persistent bottom action bar: **Save Settings**,
  **Close Window** (hides to the tray), and **Restart as administrator** (hidden
  when already elevated).
- The title-bar **X** shows the exit / minimize-to-tray prompt (with *Remember my
  choice*); the tray menu's **Exit** always quits.

## Configuration

The default config file location is:

`%AppData%\wang.station\app\WindowTopTool\config.json`

At startup the app also checks the standard PPC install directories
(`%ProgramFiles%\ppc` and `%ProgramFiles(x86)%\ppc`) for a config that already
lives at `<ppc_path>\app\config\config.json`; if one exists it is used in
preference to the `%AppData%` copy. The app does **not** automatically migrate
its config into the PPC directory — it only detects an existing one.

> **Note on version reporting:** `PpcAppVersion` is always derived from the
> running assembly version (the `<Version>` property in `WindowTopTool.csproj`),
> never from the persisted config file. A stale persisted version (e.g.
> `0.0.2`) is ignored, so the version reported to PPC always matches the build.

## PPC Integration

PPC is required from v0.0.5 onward. The app keeps a **long-lived, self-healing
connection** (managed by `PpcSession`) on a background thread so it never blocks
the UI:

1. Launches the local PPC launcher that ships next to the executable —
   `l.exe` if present, otherwise `l.ps1` run by the OS system PowerShell
   (`%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile
   -ExecutionPolicy Bypass -File`), so it executes in the system environment.
   Usually only one exists; if neither is found it still tries to connect (PPC
   may already be up).
2. Retries connecting to the PPC server at the configured host/port (default
   `127.0.0.1:9527`) every 5 seconds for up to 2 minutes. If it never connects,
   it logs a connection-timeout error and continues running without PPC.
3. Once connected, registers with `REGISTER_APP` on first run or `UPDATE_APP`
   afterwards (falling back to `REGISTER_APP` if the server reports the app is
   not registered), using the app id `WindowTopTool`, the assembly version and
   the exe path; persists the returned SHA-256 hash and authenticates (`AUTH`).
4. Verifies the server version (`PPCVERSION`); an unsupported version stops
   further retries (see the range below).
5. Keeps the link warm with a watchdog that pings every 30 seconds and
   transparently reconnects (re-handshaking) if the link drops, sending a
   `WINDOW INFO` notice on recovery.
6. Forwards every `ERROR`-level log entry to PPC as a `WINDOW ERROR` popup via
   the logger's error sink.

The connector supports **PPC v0.0.8 through v0.1.0**; versions outside this
range are rejected.

## Multi-Language Support

The UI is localized into five languages with automatic system-language
detection:

| Code | Language | Notes |
|------|----------|-------|
| `en` | English | Fallback when the system language is unmatched. |
| `zh` | Chinese (Simplified) | |
| `fr` | French | |
| `ru` | Russian | |
| `ar` | Arabic | Right-to-left (RTL) layout applied automatically. |

The active language can be overridden in the settings dialog; the choice
persists across launches. Resource files use the naming convention
`strings.<lang>.json` and contain identical key sets.

## Logging

Structured log lines follow the PPC server format:
`[YYYY-MM-DD HH:MM:SS] [LEVEL] message`, with four levels
(`DEBUG`, `INFO`, `WARN`, `ERROR`).

| Data | Path |
|------|------|
| Log | `%AppData%\wang.station\app\WindowTopTool\logs\app.log` |

The log level and local logging toggle are configurable in settings.

## Project Structure

```
TOP_APP
├── App.xaml.cs             WinUI 3 entry: single-instance guard, tray/event wiring, PPC launch/register, ExitApp
├── MainWindow.xaml.cs      WinUI 3 top-level window (SettingsView host); AppWindow icon, close prompt (exit/tray)
├── WinUI/                  WinUI 3 SettingsView (navigation, window list, general settings, admin restart, localization)
├── WindowPinManager.cs     Core pinning logic (topmost, opacity, state)
├── WindowHookManager.cs    Low-level mouse hooks (click-through, activation)
├── EdgeAutoHideManager.cs  Edge-gesture detection → PiP
├── MiniWindowManager.cs    Mini-window preview mode
├── PiPWindow.cs            Picture-in-picture overlay window
├── WindowValidator.cs      Window pin eligibility checks
├── TrayManager.cs          System tray icon and context menu (native Shell_NotifyIconW)
├── AppConfig.cs            Configuration and application data paths
├── AppLogger.cs            Local structured logger
├── PpcConnector.cs         PPC client (TCP, register/update, auth, version checks)
├── PpcSession.cs           Long-lived self-healing PPC link: launcher, connect/retry, keep-alive, error forwarding
├── PpcErrorCodes.cs        Mirrored error/status codes
├── NativeMethods.cs        P/Invoke declarations
├── StateManager.cs         Persisted application state (JSON)
├── WindowInfo.cs           Window metadata model
├── Localization/           strings.{en,zh,fr,ru,ar}.json + LocalizationManager
├── icon.ico                Application icon (taskbar + tray)
└── WindowTopTool.csproj    Project configuration
```

## Version History

| Version | Highlights |
|---------|------------|
| 0.0.9 | Window close (X) now prompts **Exit / Minimize to tray** with a persisted **Remember my choice** (`AppConfig.CloseAction`); single-instance guard (named mutex) that activates the existing window on a duplicate launch; PPC now runs as a long-lived, self-healing `PpcSession` (launches `l.exe`/`l.ps1`, connects with a 5s/2-min retry, then keep-alive pings with transparent reconnect, enforces the version check, and forwards `ERROR` logs to PPC `WINDOW ERROR` popups) doing `REGISTER_APP`/`UPDATE_APP` + `AUTH`; settings pages now fill/reflow with the window and the General page has a persistent bottom action bar; data directory corrected to `%AppData%\wang.station\app\WindowTopTool`; build command made generic (no arch flags needed). |
| 0.0.8 | Admin restart button (UAC elevation via `ProcessStartInfo Verb=runas`); window close hides to tray instead of quitting; tray Exit properly disposes services; window/taskbar icon via `AppWindow.SetIcon`; fixed CreateButton to resolve localization keys (Save/Close/Unpin/Restart buttons were showing raw key names); removed WinUI3 E_UNEXPECTED COMException on ObservableCollection mutation (replace whole `List`/`ItemsSource` instead of mutating in place); defensive null checks and single-instance guard improvements; multi-language completeness verified across 5 languages (en/zh/fr/ru/ar, 86 keys each). |
| 0.0.6 | PPC is required for normal builds; independent startup for 64-bit and 32-bit PPC; delayed post-start connection verification; existing pinned windows immediately receive saved global settings; improved double-click activation and foreground focus handling. |
| 0.0.5 | PPC v0.0.8 integration (global signature relaxation, no AUTH required); PiP mode shows live window content via PrintWindow instead of just an icon; configurable PiP size in settings; error status codes forwarded to PPC WINDOW ERROR popup; `ShowErrorWindow`/`ShowInfoWindow` on PpcConnector. |
| 0.0.4 | English-only code comments; PPC terminal auto-start with visible window; multilingual PPC connection-failure warning; PPC version range expanded to 0.0.6–0.0.7. |
| 0.0.3 | Assembly-version-sourced `PpcAppVersion`; double-click activation fix; config migration to PPC directory; multi-language (en/zh/fr/ru/ar); PPC integration. |
| 0.0.2 | PPC connector integration; local logging; error-code mirroring. |
| 0.0.1 | Initial release: pinning, click-through, mini-window, PiP, edge auto-hide, opacity, tray, state persistence. |
