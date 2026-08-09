using System;

namespace WindowTopTool
{
    /// <summary>
    /// Represents pinned window information
    /// </summary>
    public class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public byte Opacity { get; set; } = 200; // Default ~78% opacity
        public bool IsClickThrough { get; set; } = false;
        public bool IsAutoHidden { get; set; } = false;
        public bool IsMiniMode { get; set; } = false;

        // Original window position for mini mode
        public int OriginalLeft { get; set; }
        public int OriginalTop { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }

        public WindowInfo()
        {
        }

        public WindowInfo(IntPtr handle, string title, string processName)
        {
            Handle = handle;
            Title = title;
            ProcessName = processName;
        }

        public override string ToString()
        {
            return $"{Title} (PID: {ProcessName})";
        }
    }
}