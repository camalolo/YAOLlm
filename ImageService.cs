using System.Drawing;
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
            using var resizedImage = image.Resize(640, (int)(image.Height * 640.0 / image.Width));
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

    public static (string base64, string title) CaptureScreen(Logger logger, Form formToHide)
    {
        try
        {
            formToHide.Visible = false;
            Thread.Sleep(100);
            string title = GetActiveWindowTitle();
            using var screenshot = new Bitmap(Screen.PrimaryScreen?.Bounds.Width ?? 0, Screen.PrimaryScreen?.Bounds.Height ?? 0);
            using (var g = Graphics.FromImage(screenshot)) g.CopyFromScreen(0, 0, 0, 0, screenshot.Size);
            formToHide.Visible = true;
            using var ms = new MemoryStream();
            screenshot.Save(ms, ImageFormat.Png);
            string base64 = Convert.ToBase64String(ms.ToArray());
            string resizedBase64 = ResizeImageBase64(logger, base64);
            return (resizedBase64, title);
        }
        catch (Exception ex)
        {
            logger.Log($"Screen capture error: {ex.Message}");
            return (string.Empty, string.Empty);
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
