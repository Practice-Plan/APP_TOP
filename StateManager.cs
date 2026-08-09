using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowTopTool
{
    /// <summary>
    /// Manages application state persistence
    /// </summary>
    public class StateManager
    {
        private static readonly string StateFilePath = GetStateFilePath();
        private readonly List<WindowState> _savedWindows = new List<WindowState>();

        public void SaveState(IEnumerable<WindowInfo> windows)
        {
            try
            {
                var state = new ApplicationState
                {
                    LastSaved = DateTime.Now,
                    Windows = windows.Select(w => new WindowState
                    {
                        Title = w.Title,
                        ProcessName = w.ProcessName,
                        Opacity = w.Opacity,
                        IsClickThrough = w.IsClickThrough,
                        OriginalLeft = w.OriginalLeft,
                        OriginalTop = w.OriginalTop,
                        OriginalWidth = w.OriginalWidth,
                        OriginalHeight = w.OriginalHeight
                    }).ToList()
                };

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(state, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(StateFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save state: {ex.Message}");
            }
        }

        public List<WindowState> LoadState()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    var json = File.ReadAllText(StateFilePath);
                    var state = Newtonsoft.Json.JsonConvert.DeserializeObject<ApplicationState>(json);
                    return state?.Windows ?? new List<WindowState>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load state: {ex.Message}");
            }
            return new List<WindowState>();
        }

        public void ClearState()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    File.Delete(StateFilePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear state: {ex.Message}");
            }
        }

        private static string GetStateFilePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "WindowTopTool");
            if (!Directory.Exists(appFolder))
            {
                Directory.CreateDirectory(appFolder);
            }
            return Path.Combine(appFolder, "state.json");
        }
    }

    public class ApplicationState
    {
        public DateTime LastSaved { get; set; }
        public List<WindowState> Windows { get; set; } = new List<WindowState>();
    }

    public class WindowState
    {
        public string Title { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public byte Opacity { get; set; }
        public bool IsClickThrough { get; set; }
        public int OriginalLeft { get; set; }
        public int OriginalTop { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
    }
}