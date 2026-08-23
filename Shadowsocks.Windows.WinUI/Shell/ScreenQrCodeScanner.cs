#nullable enable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Shadowsocks.Windows.WinUI.Shell;

/// <summary>
/// Windows-only QR scanner used by the WinUI sharing page. Supports virtual-desktop capture,
/// image streams/files, and clipboard bitmaps without taking a dependency on Windows Forms.
/// </summary>
public static class ScreenQrCodeScanner
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static string? ScanFromScreen()
    {
        int x = GetSystemMetrics(SmXVirtualScreen);
        int y = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using var fullImage = new Bitmap(width, height);
        using (Graphics graphics = Graphics.FromImage(fullImage))
        {
            graphics.CopyFromScreen(x, y, 0, 0, fullImage.Size, CopyPixelOperation.SourceCopy);
        }

        return ScanBitmap(fullImage);
    }

    public static string? ScanFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var source = new Bitmap(path);
        return ScanBitmap(source);
    }

    public static string? ScanFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        using var bitmap = new Bitmap(image);
        return ScanBitmap(bitmap);
    }

    public static async Task<(bool HasImage, string? Result)> ScanFromClipboardAsync()
    {
        DataPackageView content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            return (false, null);
        }

        var reference = await content.GetBitmapAsync();
        using var randomStream = await reference.OpenReadAsync();
        using Stream stream = randomStream.AsStreamForRead();
        return (true, ScanFromStream(stream));
    }

    private static string? ScanBitmap(Bitmap fullImage)
    {
        const int maxTry = 10;
        for (int i = 0; i < maxTry; i++)
        {
            int marginLeft = (int)((double)fullImage.Width * i / 2.5 / maxTry);
            int marginTop = (int)((double)fullImage.Height * i / 2.5 / maxTry);
            int cropWidth = fullImage.Width - marginLeft * 2;
            int cropHeight = fullImage.Height - marginTop * 2;
            if (cropWidth <= 0 || cropHeight <= 0)
            {
                break;
            }

            var cropRect = new Rectangle(marginLeft, marginTop, cropWidth, cropHeight);
            using var target = new Bitmap(fullImage.Width, fullImage.Height);
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.DrawImage(fullImage, new Rectangle(0, 0, target.Width, target.Height), cropRect, GraphicsUnit.Pixel);
            }

            RGBLuminanceSource luminanceSource = CreateLuminanceSource(target);
            var bitmap = new BinaryBitmap(new HybridBinarizer(luminanceSource));
            Result? result = new QRCodeReader().decode(bitmap);
            if (!string.IsNullOrWhiteSpace(result?.Text))
            {
                return result.Text;
            }
        }

        return null;
    }

    private static RGBLuminanceSource CreateLuminanceSource(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int absoluteStride = Math.Abs(stride);
            byte[] source = new byte[absoluteStride * bitmap.Height];
            Marshal.Copy(data.Scan0, source, 0, source.Length);

            int packedStride = checked(bitmap.Width * 4);
            byte[] pixels = new byte[checked(packedStride * bitmap.Height)];
            for (int row = 0; row < bitmap.Height; row++)
            {
                int sourceRow = stride >= 0 ? row : bitmap.Height - 1 - row;
                Buffer.BlockCopy(source, sourceRow * absoluteStride, pixels, row * packedStride, packedStride);
            }

            return new RGBLuminanceSource(
                pixels,
                bitmap.Width,
                bitmap.Height,
                RGBLuminanceSource.BitmapFormat.BGRA32);
        }
        finally
        {
            if (data is not null)
            {
                bitmap.UnlockBits(data);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
