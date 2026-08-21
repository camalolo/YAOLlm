using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace YAOLlm;

public static class ImageService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static string ResizeImageBase64(Logger logger, string base64)
    {
        try
        {
            byte[] imageBytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(imageBytes);
            using var image = new Bitmap(ms);

            if (image.Width <= 0 || image.Height <= 0)
                return base64;

            // Only downscale — small images are sent as-is
            const int maxWidth = 640;
            if (image.Width <= maxWidth)
                return base64;

            int newWidth = maxWidth;
            int newHeight = Math.Max(1, (int)Math.Round(image.Height * (double)maxWidth / image.Width));

            using var resizedImage = new Bitmap(newWidth, newHeight);
            resizedImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);
            using (var g = Graphics.FromImage(resizedImage))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, new Rectangle(0, 0, newWidth, newHeight), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            }
            using var outputMs = new MemoryStream();
            resizedImage.Save(outputMs, ImageFormat.Png);
            return Convert.ToBase64String(outputMs.ToArray());
        }
        catch (Exception ex)
        {
            logger.Log($"Error resizing image: {ex.Message}");
            return base64;
        }
    }

    /// <summary>
    /// Captures the entire virtual screen (all monitors) and returns a
    /// downscaled base64 PNG. Call from a background thread — this blocks.
    /// The caller is responsible for hiding the overlay before calling.
    /// </summary>
    public static string CapturePrimaryScreen(Logger logger)
    {
        try
        {
            var bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                bounds = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                logger.Log("Screen capture error: no screen bounds available");
                return string.Empty;
            }

            using var screenshot = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(screenshot))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

            using var ms = new MemoryStream();
            screenshot.Save(ms, ImageFormat.Png);
            string base64 = Convert.ToBase64String(ms.ToArray());
            return ResizeImageBase64(logger, base64);
        }
        catch (Exception ex)
        {
            logger.Log($"Screen capture error: {ex.Message}");
            return string.Empty;
        }
    }

    public static string GetActiveWindowTitle()
    {
        const int nChars = 256;
        var buff = new StringBuilder(nChars);
        return GetWindowText(GetForegroundWindow(), buff, nChars) > 0 && buff.ToString() != "YAOLlm"
            ? buff.ToString()
            : string.Empty;
    }
}
