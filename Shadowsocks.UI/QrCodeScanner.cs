using System.Drawing;
using System.Windows.Forms;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.Windows.Compatibility;

namespace Shadowsocks.UI
{
    internal static class QrCodeScanner
    {
        public static string ScanFromScreen()
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                using Bitmap fullImage = new(screen.Bounds.Width, screen.Bounds.Height);
                using (Graphics graphics = Graphics.FromImage(fullImage))
                    graphics.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, fullImage.Size, CopyPixelOperation.SourceCopy);

                const int maxTry = 10;
                for (int i = 0; i < maxTry; i++)
                {
                    int marginLeft = (int)((double)fullImage.Width * i / 2.5 / maxTry);
                    int marginTop = (int)((double)fullImage.Height * i / 2.5 / maxTry);
                    Rectangle cropRect = new(marginLeft, marginTop, fullImage.Width - marginLeft * 2, fullImage.Height - marginTop * 2);
                    using Bitmap target = new(screen.Bounds.Width, screen.Bounds.Height);
                    using (Graphics graphics = Graphics.FromImage(target))
                        graphics.DrawImage(fullImage, new Rectangle(0, 0, target.Width, target.Height), cropRect, GraphicsUnit.Pixel);

                    BinaryBitmap bitmap = new(new HybridBinarizer(new BitmapLuminanceSource(target)));
                    var result = new QRCodeReader().decode(bitmap);
                    if (result != null) return result.Text;
                }
            }
            return null;
        }
    }
}
