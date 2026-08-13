using System;
using System.Drawing;
using System.Windows.Forms;
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


        #region Size

        [JsonIgnore]
        public int Width
        {
            get { return width; }
            set { width = value; }
        }

        [JsonIgnore]
        public int Height
        {
            get { return height; }
            set { height = value; }
        }

        [JsonIgnore]
        public int Top
        {
            get { return top; }
            set { top = value; }
        }

        [JsonIgnore]
        public int Left
        {
            get { return left; }
            set { left = value; }
        }

        [JsonIgnore]
        public bool Maximized
        {
            get { return maximized; }
            set { maximized = value; }
        }

        [JsonIgnore]
        // Use GetBestTop() and GetBestLeft() to ensure the log viwer form can be always display IN screen. 
        public int BestLeft
        {
            get
            {
                int width = Width;
                width = (width >= 400) ? width : 400; // set up the minimum size
                return Screen.PrimaryScreen.WorkingArea.Width - width;
            }
        }

        [JsonIgnore]
        public int BestTop
        {
            get
            {
                int height = Height;
                height = (height >= 200) ? height : 200; // set up the minimum size
                return Screen.PrimaryScreen.WorkingArea.Height - height;
            }
        }

        #endregion

    }
}
