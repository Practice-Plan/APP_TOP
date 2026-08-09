using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowTopTool
{
    /// <summary>
    /// 鼠标穿透管理器
    ///
    /// 核心机制：
    /// 1. WS_EX_TRANSPARENT — 对非活跃置顶窗口自动添加此样式，
    ///    使单击/悬停事件被操作系统直接传递到下层窗口（零延迟、零闪烁）。
    /// 2. WH_MOUSE_LL 低级鼠标钩子 — 检测双击模式，当用户在非活跃置顶窗口区域
    ///    连续两次点击时，激活该窗口（移除 WS_EX_TRANSPARENT 并 SetForegroundWindow）。
    /// 3. SetWinEventHook(EVENT_SYSTEM_FOREGROUND) — 监听前台窗口变化，
    ///    当置顶窗口获得前台时移除 WS_EX_TRANSPARENT，
    ///    当置顶窗口失去前台时重新添加 WS_EX_TRANSPARENT。
    ///
    /// 行为规则：
    /// - 非活跃置顶窗口：单击穿透到下层窗口，双击激活置顶窗口
    /// - 活跃置顶窗口：所有鼠标操作正常作用于置顶窗口
    /// </summary>
    public class WindowHookManager : IDisposable
    {
        // ── 常量 ──────────────────────────────────────────────

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        /// <summary>双击判定最大间隔（毫秒）</summary>
        private const int DOUBLE_CLICK_INTERVAL = 400;
        /// <summary>双击判定最大位移（像素）</summary>
        private const int DOUBLE_CLICK_TOLERANCE = 8;

        // ── 委托与结构体 ──────────────────────────────────────

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr WinEventProcType(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public NativeMethods.POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ── P/Invoke ──────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProcType lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetForegroundWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        // ── 字段 ──────────────────────────────────────────────

        private readonly WindowPinManager _pinManager;
        private IntPtr _mouseHookID = IntPtr.Zero;
        private IntPtr _winEventHook = IntPtr.Zero;
        private LowLevelMouseProc? _mouseProc;
        private WinEventProcType? _winEventProc;
        private bool _disposed = false;

        // 双击检测状态
        private DateTime _lastClickTime = DateTime.MinValue;
        private Point _lastClickPos = Point.Empty;
        private IntPtr _lastClickTargetWindow = IntPtr.Zero;

        /// <summary>正在激活的窗口句柄，防止前台钩子过早重新添加穿透样式</summary>
        private IntPtr _activatingWindow = IntPtr.Zero;

        /// <summary>
        /// 激活重试计数器。当延迟刷新检测到置顶窗口仍未获得前台时，
        /// 最多重试 <see cref="MAX_ACTIVATION_RETRIES"/> 次。
        /// </summary>
        private int _activationRetryCount;
        private const int MAX_ACTIVATION_RETRIES = 3;

        /// <summary>延迟刷新定时器，确保前台变化后正确更新穿透状态</summary>
        private System.Windows.Forms.Timer? _delayedRefreshTimer;

        // ── 构造与初始化 ────────────────────────────────────

        public WindowHookManager(WindowPinManager pinManager)
        {
            _pinManager = pinManager;
            InstallHooks();
            _pinManager.WindowPinned += OnWindowPinned;
            _pinManager.WindowUnpinned += OnWindowUnpinned;
            _pinManager.WindowsChanged += OnWindowsChanged;
        }

        private void InstallHooks()
        {
            // 安装低级鼠标钩子
            _mouseProc = MouseHookCallback;
            using (var process = Process.GetCurrentProcess())
            using (var module = process.MainModule)
            {
                _mouseHookID = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(module?.ModuleName), 0);
            }
            if (_mouseHookID == IntPtr.Zero)
            {
                Debug.WriteLine("鼠标低级钩子安装失败");
                AppLogger.Error(
                    "Failed to install low-level mouse hook (WH_MOUSE_LL)",
                    PpcErrorCodes.ErrorHookInstallFailed);
            }

            // 安装前台窗口变化事件钩子
            _winEventProc = ForegroundChangedCallback;
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0, 0,
                WINEVENT_OUTOFCONTEXT
            );
            if (_winEventHook == IntPtr.Zero)
            {
                Debug.WriteLine("前台窗口变化事件钩子安装失败");
                AppLogger.Error(
                    "Failed to install foreground-window event hook (EVENT_SYSTEM_FOREGROUND)",
                    PpcErrorCodes.ErrorHookInstallFailed);
            }

            // 初始化时更新所有已置顶窗口的穿透状态
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── 事件回调 ─────────────────────────────────────────

        /// <summary>
        /// 低级鼠标钩子回调 — 检测双击模式以激活非活跃置顶窗口
        /// </summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var message = wParam.ToInt32();
                var mousePos = new Point(hookStruct.pt.X, hookStruct.pt.Y);

                if (message == WM_LBUTTONDOWN)
                {
                    // When a double-click activates a pinned window, suppress
                    // this (second) click so it is NOT delivered to the window
                    // beneath. Without suppression, the underlying application
                    // receives a double-click, performs its default action, and
                    // steals the foreground — undoing our activation and
                    // re-transparenting the pinned window.
                    if (HandleLeftButtonDown(mousePos))
                    {
                        // Returning a non-zero value without calling
                        // CallNextHookEx swallows the mouse message.
                        return (IntPtr)1;
                    }
                }
            }

            // 始终传递，不阻断任何鼠标消息
            return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
        }

        /// <summary>
        /// 前台窗口变化回调 — 更新所有置顶窗口的 WS_EX_TRANSPARENT 状态
        /// </summary>
        private IntPtr ForegroundChangedCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject == 0 && idChild == 0)
            {
                // 前台窗口已变化，更新所有置顶窗口的穿透状态
                UpdateAllPinnedWindowsClickThrough();
            }
            return IntPtr.Zero;
        }

        // ── 双击检测逻辑 ────────────────────────────────────

        /// <summary>
        /// 处理左键按下事件，检测对非活跃置顶窗口的双击。
        /// </summary>
        /// <returns>
        /// 当此次点击完成了对置顶窗口的双击并触发了激活时返回 <c>true</c>，
        /// 调用方应吞掉此鼠标消息以防其传递到下层窗口。
        /// 其他情况返回 <c>false</c>，消息正常传递。
        /// </returns>
        private bool HandleLeftButtonDown(Point mousePos)
        {
            var now = DateTime.Now;

            // 查找鼠标位置下的非活跃置顶窗口
            var targetWindow = FindPinnedWindowAtPoint(mousePos);
            if (targetWindow == IntPtr.Zero)
            {
                _lastClickTime = DateTime.MinValue;
                _lastClickTargetWindow = IntPtr.Zero;
                return false;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            bool isTargetActive = targetWindow == foreground;

            // 如果目标窗口已经是活跃窗口，不需要处理
            if (isTargetActive)
            {
                _lastClickTime = DateTime.MinValue;
                _lastClickTargetWindow = IntPtr.Zero;
                return false;
            }

            // 检测双击模式：同一窗口、相近位置、时间间隔内
            bool isDoubleClick = (now - _lastClickTime).TotalMilliseconds <= DOUBLE_CLICK_INTERVAL
                && _lastClickTargetWindow == targetWindow
                && Math.Abs(mousePos.X - _lastClickPos.X) <= DOUBLE_CLICK_TOLERANCE
                && Math.Abs(mousePos.Y - _lastClickPos.Y) <= DOUBLE_CLICK_TOLERANCE;

            if (isDoubleClick)
            {
                // 双击 — 激活置顶窗口
                ActivatePinnedWindow(targetWindow);
                _lastClickTime = DateTime.MinValue;
                _lastClickTargetWindow = IntPtr.Zero;
                // 吞掉此第二次点击，防止下层应用接收到双击而抢夺前台。
                return true;
            }
            else
            {
                // 记录此次点击，等待可能的第二次点击
                _lastClickTime = now;
                _lastClickPos = mousePos;
                _lastClickTargetWindow = targetWindow;
                return false;
            }
        }

        /// <summary>
        /// 查找鼠标坐标下的非活跃置顶窗口（使用 GetWindowRect 手动检测）
        /// </summary>
        private IntPtr FindPinnedWindowAtPoint(Point pt)
        {
            var foreground = NativeMethods.GetForegroundWindow();

            foreach (var window in _pinManager.PinnedWindows)
            {
                // 跳过已活跃的窗口
                if (window.Handle == foreground)
                    continue;

                if (!NativeMethods.IsWindow(window.Handle))
                    continue;

                if (NativeMethods.GetWindowRect(window.Handle, out var rect))
                {
                    if (pt.X >= rect.Left && pt.X <= rect.Right &&
                        pt.Y >= rect.Top && pt.Y <= rect.Bottom)
                    {
                        return window.Handle;
                    }
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 激活置顶窗口：移除穿透样式并设为前台
        /// 关键修复：SetForegroundWindow 是异步的，调用后 GetForegroundWindow 可能仍返回旧窗口，
        /// 所以不能立即调用 UpdateAllPinnedWindowsClickThrough（否则会重新添加穿透样式）。
        /// 解决方案：使用 AttachThreadInput 强制前台切换 + 延迟定时器刷新。
        /// </summary>
        private void ActivatePinnedWindow(IntPtr hWnd)
        {
            try
            {
                // 标记正在激活此窗口，防止前台钩子过早重新添加穿透样式
                _activatingWindow = hWnd;

                // 先移除穿透样式，确保窗口能接收鼠标事件
                SetWindowTransparent(hWnd, false);

                // 使用 AttachThreadInput 技术强制前台切换
                // 这能绕过 SetForegroundWindow 的限制（后台进程通常无法设置前台）
                ForceSetForegroundWindow(hWnd);

                Debug.WriteLine($"已通过双击激活置顶窗口: {hWnd}");

                // 不立即调用 UpdateAllPinnedWindowsClickThrough
                // 使用延迟定时器等待前台变化生效后再刷新
                ScheduleDelayedRefresh(150);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"激活置顶窗口失败: {ex.Message}");
                _activatingWindow = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 使用 AttachThreadInput 强制设置前台窗口。
        ///
        /// 改进点：
        /// - 先 <see cref="ShowWindow"/> 恢复最小化的窗口，否则 SetForegroundWindow
        ///   对最小化窗口无效。
        /// - 同时附加到「当前前台线程」与「目标窗口线程」的输入队列后再调用
        ///   SetForegroundWindow，绕过前台锁定限制。
        /// - 调用 <see cref="BringWindowToTop"/> 确保 Z-order 置顶。
        /// - 在 finally 中解除所有 AttachThreadInput，避免输入队列长期共享。
        /// </summary>
        private void ForceSetForegroundWindow(IntPtr hWnd)
        {
            try
            {
                // 恢复最小化/最大化的窗口，确保能被激活
                NativeMethods.ShowWindow(hWnd, SW_RESTORE);

                var currentThread = GetCurrentThreadId();
                var foregroundWnd = NativeMethods.GetForegroundWindow();

                uint foregroundThread = 0;
                if (foregroundWnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(foregroundWnd, out foregroundThread);
                }

                uint targetThread = 0;
                GetWindowThreadProcessId(hWnd, out targetThread);

                // 同时附加到前台线程与目标线程的输入队列
                bool attachedToForeground = foregroundThread != 0
                    && foregroundThread != currentThread
                    && foregroundThread != targetThread;
                bool attachedToTarget = targetThread != 0
                    && targetThread != currentThread;
                if (attachedToForeground)
                    AttachThreadInput(currentThread, foregroundThread, true);
                if (attachedToTarget)
                    AttachThreadInput(currentThread, targetThread, true);

                try
                {
                    BringWindowToTop(hWnd);
                    NativeMethods.SetForegroundWindow(hWnd);
                    NativeMethods.SetForegroundWindow(hWnd);
                }
                finally
                {
                    if (attachedToForeground)
                        AttachThreadInput(currentThread, foregroundThread, false);
                    if (attachedToTarget)
                        AttachThreadInput(currentThread, targetThread, false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ForceSetForegroundWindow 失败: {ex.Message}");
                // 降级为普通 SetForegroundWindow
                NativeMethods.SetForegroundWindow(hWnd);
            }
        }

        /// <summary>
        /// 延迟刷新穿透状态（等待前台变化生效后执行）。
        ///
        /// 改进点：到达延迟时间后先校验置顶窗口是否真正获得了前台。
        /// 若未获得（典型情况：第二次双击点击被下层应用抢夺前台），
        /// 则重试激活，最多 <see cref="MAX_ACTIVATION_RETRIES"/> 次。
        /// 只有确认前台已切换或重试耗尽后，才清除 <see cref="_activatingWindow"/>
        /// 并刷新穿透状态，避免过早重新添加 WS_EX_TRANSPARENT 导致置顶窗口
        /// 「无法活跃」。
        /// </summary>
        private void ScheduleDelayedRefresh(int delayMs)
        {
            _delayedRefreshTimer?.Stop();
            _delayedRefreshTimer?.Dispose();
            _activationRetryCount = 0;
            _delayedRefreshTimer = new System.Windows.Forms.Timer { Interval = delayMs };
            _delayedRefreshTimer.Tick += OnDelayedRefreshTick;
            _delayedRefreshTimer.Start();
        }

        private void OnDelayedRefreshTick(object? sender, EventArgs e)
        {
            _delayedRefreshTimer?.Stop();

            var foreground = NativeMethods.GetForegroundWindow();
            if (_activatingWindow != IntPtr.Zero
                && foreground != _activatingWindow
                && _activationRetryCount < MAX_ACTIVATION_RETRIES)
            {
                // 前台切换未生效（可能被其他应用抢夺），重试激活
                _activationRetryCount++;
                Debug.WriteLine($"置顶窗口激活未生效，重试 ({_activationRetryCount}/{MAX_ACTIVATION_RETRIES})");
                ForceSetForegroundWindow(_activatingWindow);
                _delayedRefreshTimer!.Interval = 100;
                _delayedRefreshTimer.Start();
                return;
            }

            _activatingWindow = IntPtr.Zero;
            _activationRetryCount = 0;
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── WS_EX_TRANSPARENT 动态管理 ───────────────────────

        /// <summary>
        /// 更新所有置顶窗口的穿透状态：
        /// - 活跃（前台）置顶窗口 → 移除 WS_EX_TRANSPARENT（正常交互）
        /// - 非活跃置顶窗口 → 添加 WS_EX_TRANSPARENT（单击穿透）
        /// - 正在激活中的窗口 → 保持无穿透（等待前台变化生效）
        /// </summary>
        private void UpdateAllPinnedWindowsClickThrough()
        {
            var foreground = NativeMethods.GetForegroundWindow();

            foreach (var window in _pinManager.PinnedWindows)
            {
                if (!NativeMethods.IsWindow(window.Handle))
                    continue;

                // 正在激活中的窗口保持无穿透样式
                if (window.Handle == _activatingWindow)
                {
                    SetWindowTransparent(window.Handle, false);
                    continue;
                }

                bool shouldBeTransparent = window.Handle != foreground;
                SetWindowTransparent(window.Handle, shouldBeTransparent);
            }
        }

        /// <summary>
        /// 设置或移除窗口的 WS_EX_TRANSPARENT 样式
        /// </summary>
        private void SetWindowTransparent(IntPtr hWnd, bool transparent)
        {
            try
            {
                var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                var hasTransparent = (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0;

                if (transparent && !hasTransparent)
                {
                    exStyle |= NativeMethods.WS_EX_TRANSPARENT;
                    NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);
                }
                else if (!transparent && hasTransparent)
                {
                    exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
                    NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE, exStyle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置窗口穿透样式失败: {ex.Message}");
            }
        }

        // ── 置顶窗口事件处理 ─────────────────────────────────

        private void OnWindowPinned(object? sender, WindowPinEventArgs e)
        {
            // 新窗口被置顶时，根据当前前台状态设置穿透
            var foreground = NativeMethods.GetForegroundWindow();
            bool shouldBeTransparent = e.Window.Handle != foreground;
            SetWindowTransparent(e.Window.Handle, shouldBeTransparent);

            // 其他已置顶窗口如果不再是前台，确保它们也设为穿透
            UpdateAllPinnedWindowsClickThrough();
        }

        private void OnWindowUnpinned(object? sender, WindowPinEventArgs e)
        {
            // 窗口被取消置顶时，移除穿透样式恢复正常
            SetWindowTransparent(e.Window.Handle, false);
        }

        private void OnWindowsChanged(object? sender, EventArgs e)
        {
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── 公开方法 ────────────────────────────────────────

        /// <summary>
        /// 手动刷新所有置顶窗口的穿透状态
        /// </summary>
        public void RefreshClickThroughState()
        {
            UpdateAllPinnedWindowsClickThrough();
        }

        // ── 资源清理 ─────────────────────────────────────────

        public void Dispose()
        {
            if (!_disposed)
            {
                // 移除所有置顶窗口的穿透样式
                foreach (var window in _pinManager.PinnedWindows)
                {
                    if (NativeMethods.IsWindow(window.Handle))
                    {
                        SetWindowTransparent(window.Handle, false);
                    }
                }

                _delayedRefreshTimer?.Stop();
                _delayedRefreshTimer?.Dispose();

                if (_mouseHookID != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHookID);
                    _mouseHookID = IntPtr.Zero;
                }

                if (_winEventHook != IntPtr.Zero)
                {
                    UnhookWinEvent(_winEventHook);
                    _winEventHook = IntPtr.Zero;
                }

                _pinManager.WindowPinned -= OnWindowPinned;
                _pinManager.WindowUnpinned -= OnWindowUnpinned;
                _pinManager.WindowsChanged -= OnWindowsChanged;

                _disposed = true;
            }
        }
    }
}

