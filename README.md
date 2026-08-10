# Window Top Tool

## Overview

**Window Top Tool** is a lightweight Windows utility for pinning windows
always-on-top, with click-through, mini-window, picture-in-picture, edge
auto-hide, opacity control, and state persistence. It runs from the system
tray, supports global hotkeys, and integrates **optionally** with the
[PPC Central Processing System](../main/README.md) for centralized
application management and logging.

- **Version:** 0.0.4
- **Target:** .NET 8.0 (`net8.0-windows`, WinForms)
- **License:** GPL-3.0 (see `LICENSE`)

## Key Features

### 📌 Window Pinning
Keep any window always on top. Operate via the tray menu, global hotkeys, or
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
(20%–100%) and via hotkeys.

### 💾 State Persistence
Automatically saves the pinned window list and restores it after restart.

### 🟢 System Tray
A persistent tray icon with a right-click menu for quick actions; the icon
shows the current pinned window count.

## System Requirements

| Requirement | Detail |
|-------------|--------|
| **Operating System** | Windows 10 (1809+) or Windows 11, x64. Uses WinForms, `SetWindowPos`, global hotkeys (`RegisterHotKey`), and low-level mouse hooks (`SetWindowsHookEx`). |
| **Runtime** | .NET 8.0 Desktop Runtime. Alternatively, publish self-contained so no runtime install is needed. |
| **Permissions** | **Administrator recommended.** Required to pin windows owned by elevated processes. Without admin, higher-privilege windows may be unpinnable (the app prompts on launch). |
| **PPC (optional)** | PPC server v0.0.6 running locally on `127.0.0.1:9527`. Only needed when PPC integration is enabled; **not** required for portable mode. |
| **Disk** | < 50 MB (framework-dependent). Self-contained portable build ~150 MB. |

## Installation

### Option A — Installed (framework-dependent)

```bash
dotnet build -c Release
# Output: bin\Release\net8.0-windows\WindowTopTool.exe
```

Requires the .NET 8.0 Desktop Runtime to be installed on the target machine.
Configuration and logs are stored under `%AppData%\WindowTopTool\`.

### Option B — Portable (self-contained, no PPC)

```bash
# Build with the portable marker (single-binary, self-contained distribution)
dotnet publish -c Release -p:Portable=true -r win-x64 --self-contained true
```

The publish output contains an empty `WindowTopTool.portable` marker file next
to the executable. At runtime its presence switches the app into
**self-contained portable mode**:

- Configuration is stored in `<exe_dir>\config\config.json`
- Logs are written to `<exe_dir>\logs\app.log`
- **PPC is never invoked** (no connect, no auto-start, no config migration),
  regardless of the persisted `PpcEnabled` value.

> You can also turn any build output into a portable distribution by manually
> creating an empty file named `WindowTopTool.portable` next to the exe.
> Remove the file to return to installed mode.

### Build Configuration Determinism

| Command | Marker file | Runtime mode |
|---------|-------------|--------------|
| `dotnet build -c Release` | removed | Installed |
| `dotnet build -c Release -p:Portable=true` | created | Portable |
| `dotnet publish -c Release -p:Portable=true` | created | Portable |

The build output always reflects the requested configuration: a normal build
removes any marker left by a previous portable build, and a portable build
recreates it.

### System Tray

- **Left-click** the tray icon to open the settings dialog.
- **Right-click** for quick actions: pin active window, unpin all, settings, exit.
- The tray icon badge reflects the current pinned window count.

### Settings Dialog

- **Window management** tab: double-click a window name to pin/unpin it;
  search/filter the open window list.
- **General settings** tab: opacity, edge threshold, auto-save interval,
  notification preferences, language, hotkey customization.
- **About** tab: feature overview and version info.

## Configuration

The config file location depends on the runtime mode:

| Mode | Config path |
|------|-------------|
| Installed | `%AppData%\WindowTopTool\config.json` |
| Portable | `<exe_dir>\config\config.json` |
| PPC-managed (migrated) | `<ppc_path>\app\config\config.json` |

When PPC integration is active and the app first connects to a PPC v0.0.6
server, it queries `PPCPATH` and migrates the config to
`<ppc_path>\app\config\config.json` so settings follow the PPC installation.
On subsequent launches the migrated location is auto-detected.

> **Note on version reporting:** `PpcAppVersion` is always derived from the
> running assembly version (the `<Version>` property in `WindowTopTool.csproj`),
> never from the persisted config file. A stale persisted version (e.g.
> `0.0.2`) is ignored, so the version reported to PPC always matches the build.

## PPC Integration (Optional)

PPC integration is an optional telemetry/management channel — the app works
fully without it. When enabled in settings (`PpcEnabled=true`), the app:

1. Connects to the PPC server at the configured host/port (default
   `127.0.0.1:9527`).
2. If the server is unreachable, attempts to auto-start it from the system
   `PATH`, then `C:\Program Files\ppc\ppc.exe`, then
   `C:\Program Files (x86)\ppc\ppc.exe`.
3. Registers itself (`REGISTER_APP`) with app id `WindowTopTool` and the
   assembly version, persisting the returned hash for subsequent
   authentication (`AUTH`).
4. Migrates the config to the PPC directory (see above).
5. Forwards log entries and error codes to PPC.

The connector strictly requires **PPC v0.0.6** (minimum and maximum); older
servers are rejected. In **portable mode**, PPC integration is fully disabled.

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

| Mode | Log path |
|------|----------|
| Installed | `%AppData%\WindowTopTool\logs\app.log` |
| Portable | `<exe_dir>\logs\app.log` |

The log level and local logging toggle are configurable in settings.

## Project Structure

```
TOP_APP
├── Program.cs              Entry point, single-instance guard, admin check
├── MainForm.cs             Main UI, hotkey/tray wiring, PPC initialization
├── SettingsForm.cs         Settings dialog (window mgmt / general / about)
├── WindowPinManager.cs     Core pinning logic (topmost, opacity, state)
├── WindowHookManager.cs    Low-level mouse hooks (click-through, activation)
├── EdgeAutoHideManager.cs  Edge-gesture detection → PiP
├── MiniWindowManager.cs    Mini-window preview mode
├── PiPWindow.cs            Picture-in-picture overlay window
├── BookmarkForm.cs         Edge bookmark UI
├── WindowStateProtector.cs Pinned-state restoration/protection
├── WindowValidator.cs      Window pin eligibility checks
├── HotkeyManager.cs        Global hotkey registration
├── TrayManager.cs          System tray icon and context menu
├── AppConfig.cs            Configuration, paths, portable-mode detection
├── AppLogger.cs            Local structured logger
├── PpcConnector.cs         PPC client (TCP, auth, version checks)
├── PpcErrorCodes.cs        Mirrored error/status codes
├── NativeMethods.cs        P/Invoke declarations
├── StateManager.cs         Persisted application state (JSON)
├── WindowInfo.cs           Window metadata model
├── Localization/           strings.{en,zh,fr,ru,ar}.json + manager
├── icon.ico                Application icon
└── WindowTopTool.csproj    Project + portable build targets
```

## Version History

| Version | Highlights |
|---------|------------|
| 0.0.4 | English-only code comments; PPC terminal auto-start with visible window; multilingual PPC connection-failure warning; PPC version range expanded to 0.0.6–0.0.7. |
| 0.0.3 | Portable mode; assembly-version-sourced `PpcAppVersion`; double-click activation fix; config migration to PPC directory; multi-language (en/zh/fr/ru/ar); PPC v0.0.6 integration. |
| 0.0.2 | PPC connector integration; local logging; error-code mirroring. |
| 0.0.1 | Initial release: pinning, click-through, mini-window, PiP, edge auto-hide, opacity, tray, state persistence. |
