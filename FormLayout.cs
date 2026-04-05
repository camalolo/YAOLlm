using System.Drawing;
using System.Windows.Forms;

namespace YAOLlm;

public static class FormLayout
{
    public static void ConfigureForm(Form form)
    {
        form.FormBorderStyle = FormBorderStyle.None;
        form.Opacity = 0.9;
        form.TopMost = true;
        form.BackColor = Color.Black;
        form.Size = Screen.PrimaryScreen?.Bounds.Size ?? new Size(0, 0);
        form.Location = new Point(0, 0);
        form.Visible = false;
    }
}
