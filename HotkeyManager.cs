using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// Manages global hotkeys registration and handling
    /// </summary>
    public class HotkeyManager : IDisposable
    {
        private readonly IntPtr _windowHandle;
        private readonly Dictionary<int, Action> _hotkeyActions = new Dictionary<int, Action>();
        private bool _disposed = false;

        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

        public HotkeyManager(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
        }

        private int _nextAutoId = 100; // Start auto-IDs at 100 to avoid conflicts with config IDs

        /// <summary>
        /// Register a global hotkey with auto-assigned ID (convenience overload)
        /// </summary>
        public bool RegisterHotkey(int modifiers, int key, Action action)
        {
            var id = _nextAutoId++;
            return RegisterHotKey(id, modifiers, key, action);
        }

        /// <summary>
        /// Register a global hotkey
        /// </summary>
        public bool RegisterHotKey(int id, int modifiers, int key, Action? action = null)
        {
            if (_hotkeyActions.ContainsKey(id))
            {
                UnregisterHotKey(id);
            }

            var result = NativeMethods.RegisterHotKey(_windowHandle, id, modifiers, key);

            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"Failed to register hotkey {id}. Error code: {error}");
                AppLogger.Error(
                    $"Failed to register hotkey id={id} (Win32 error {error})",
                    PpcErrorCodes.ErrorHotkeyRegisterFailed);
                return false;
            }

            if (action != null)
            {
                _hotkeyActions[id] = action;
            }

            return true;
        }

        /// <summary>
        /// Unregister a hotkey
        /// </summary>
        public bool UnregisterHotKey(int id)
        {
            if (_hotkeyActions.ContainsKey(id))
            {
                _hotkeyActions.Remove(id);
            }

            return NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        /// <summary>
        /// Handle WM_HOTKEY message
        /// </summary>
        public void HandleHotkey(Message m)
        {
            var id = m.WParam.ToInt32();

            if (_hotkeyActions.TryGetValue(id, out var action))
            {
                action?.Invoke();
            }

            HotkeyPressed?.Invoke(this, new HotkeyEventArgs(id));
        }

        /// <summary>
        /// Register default hotkeys
        /// </summary>
        public bool RegisterDefaultHotkeys()
        {
            var config = AppConfig.Instance;
            bool allSuccess = true;

            // Pin/Unpin hotkey: Ctrl+Alt+P
            if (!RegisterHotKey(
                config.PinHotKeyId,
                config.PinHotkeyModifier,
                config.PinHotkeyKey
            ))
            {
                allSuccess = false;
                System.Diagnostics.Debug.WriteLine($"Failed to register pin hotkey (Ctrl+Alt+P)");
            }

            // Click-through toggle: Ctrl+Alt+T
            if (!RegisterHotKey(
                config.ClickThroughHotKeyId,
                config.ClickThroughHotkeyModifier,
                config.ClickThroughHotkeyKey
            ))
            {
                allSuccess = false;
                System.Diagnostics.Debug.WriteLine($"Failed to register click-through hotkey (Ctrl+Alt+T)");
            }

            // Mini mode toggle: Ctrl+Alt+M
            if (!RegisterHotKey(
                config.MiniModeHotKeyId,
                config.MiniModeHotkeyModifier,
                config.MiniModeHotkeyKey
            ))
            {
                allSuccess = false;
                System.Diagnostics.Debug.WriteLine($"Failed to register mini mode hotkey (Ctrl+Alt+M)");
            }

            // Increase opacity: Ctrl+Alt++
            if (!RegisterHotKey(
                config.IncreaseOpacityHotKeyId,
                config.IncreaseOpacityHotkeyModifier,
                config.IncreaseOpacityHotkeyKey
            ))
            {
                allSuccess = false;
                System.Diagnostics.Debug.WriteLine($"Failed to register increase opacity hotkey (Ctrl+Alt++)");
            }

            // Decrease opacity: Ctrl+Alt+-
            if (!RegisterHotKey(
                config.DecreaseOpacityHotKeyId,
                config.DecreaseOpacityHotkeyModifier,
                config.DecreaseOpacityHotkeyKey
            ))
            {
                allSuccess = false;
                System.Diagnostics.Debug.WriteLine($"Failed to register decrease opacity hotkey (Ctrl+Alt+-)");
            }

            return allSuccess;
        }

        /// <summary>
        /// Unregister all hotkeys
        /// </summary>
        public void UnregisterAll()
        {
            foreach (var id in _hotkeyActions.Keys)
            {
                NativeMethods.UnregisterHotKey(_windowHandle, id);
            }
            _hotkeyActions.Clear();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                UnregisterAll();
                _disposed = true;
            }
        }
    }

    public class HotkeyEventArgs : EventArgs
    {
        public int HotkeyId { get; }

        public HotkeyEventArgs(int hotkeyId)
        {
            HotkeyId = hotkeyId;
        }
    }
}