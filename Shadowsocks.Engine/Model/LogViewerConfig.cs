using System;
using System.Drawing;
using Newtonsoft.Json;

namespace Shadowsocks.Model
{
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

        public Font Font { get; set; } = new Font("Consolas", 8F);
        public Color BackgroundColor { get; set; } = Color.Black;
        public Color TextColor { get; set; } = Color.White;

        public LogViewerConfig()
        {
            topMost = false;
            wrapText = false;
            toolbarShown = false;
        }

        [JsonIgnore] public int Width { get => width; set => width = value; }
        [JsonIgnore] public int Height { get => height; set => height = value; }
        [JsonIgnore] public int Top { get => top; set => top = value; }
        [JsonIgnore] public int Left { get => left; set => left = value; }
        [JsonIgnore] public bool Maximized { get => maximized; set => maximized = value; }
    }
}
