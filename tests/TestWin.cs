// A plain coloured window for testing, so test runs never touch the user's real apps.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

static class TestWin
{
    [STAThread]
    static void Main()
    {
        string name = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
        Color c = name.EndsWith("A") ? Color.IndianRed : name.EndsWith("B") ? Color.SeaGreen : Color.SteelBlue;
        var form = new Form { Text = name, BackColor = c, Width = 700, Height = 450, StartPosition = FormStartPosition.Manual, Left = 50, Top = 80 };
        // Horizontal bars make a perspective warp easy to see, and keep the window from counting as blank.
        form.Paint += (s, e) =>
        {
            for (int y = 40; y < form.ClientSize.Height; y += 60)
                e.Graphics.FillRectangle(Brushes.White, 30, y, form.ClientSize.Width - 60, 20);
        };
        Application.Run(form);
    }
}
