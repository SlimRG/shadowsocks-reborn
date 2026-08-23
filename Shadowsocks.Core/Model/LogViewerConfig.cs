using System;

namespace Shadowsocks.Model
{
    /// <summary>
    /// Platform-neutral persisted state for the log viewer. UI-specific Font/Color
    /// objects are materialized only by the presentation layer.
    /// </summary>
    [Serializable]
    public class LogViewerConfig
    {
        public bool topMost { get; set; }
        public bool wrapText { get; set; }
        public bool toolbarShown { get; set; }
        public int width { get; set; } = 600;
        public int height { get; set; } = 400;
        public int top { get; set; }
        public int left { get; set; }
        public bool maximized { get; set; } = true;

        public string fontFamily { get; set; } = "Consolas";
        public float fontSize { get; set; } = 8F;
        public int fontStyle { get; set; }
        public int backgroundArgb { get; set; } = unchecked((int)0xFF000000);
        public int textArgb { get; set; } = unchecked((int)0xFFFFFFFF);

        public LogViewerConfig()
        {
            topMost = false;
            wrapText = false;
            toolbarShown = true;
        }

    }
}
