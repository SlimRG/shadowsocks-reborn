using System;
using Newtonsoft.Json;

namespace Shadowsocks.Model
{
    /// <summary>
    /// Platform-neutral persisted state for the log viewer. UI-specific Font/Color
    /// objects are materialized only by the presentation layer.
    /// </summary>
    [Serializable]
    public class LogViewerConfig
    {
        public bool topMost;
        public bool wrapText;
        public bool toolbarShown;
        public int width = 600;
        public int height = 400;
        public int top;
        public int left;
        public bool maximized = true;

        public string fontFamily = "Consolas";
        public float fontSize = 8F;
        public int fontStyle;
        public int backgroundArgb = unchecked((int)0xFF000000);
        public int textArgb = unchecked((int)0xFFFFFFFF);

        public LogViewerConfig()
        {
            topMost = false;
            wrapText = false;
            toolbarShown = true;
        }

        [JsonIgnore] public int Width { get => width; set => width = value; }
        [JsonIgnore] public int Height { get => height; set => height = value; }
        [JsonIgnore] public int Top { get => top; set => top = value; }
        [JsonIgnore] public int Left { get => left; set => left = value; }
        [JsonIgnore] public bool Maximized { get => maximized; set => maximized = value; }
    }
}
