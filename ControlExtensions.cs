using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YAOLlm;

public static class ControlExtensions
{
    public static void InvokeIfRequired(this Control control, Action action)
    {
        if (control.InvokeRequired)
            control.Invoke(action);
        else
            action();
    }

    public static Bitmap Resize(this Bitmap image, int width, int height)
    {
        var destImage = new Bitmap(width, height);
        destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);
        using (var g = Graphics.FromImage(destImage))
            g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        return destImage;
    }

    public static async Task InvokeIfRequiredAsync(this Control control, Func<Task> action)
    {
        if (control.InvokeRequired)
            await Task.Run(() => control.Invoke(action));
        else
            await action();
    }
}
