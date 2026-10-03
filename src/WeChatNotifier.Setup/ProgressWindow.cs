using System.Drawing;
using System.Windows.Forms;

namespace WeChatNotifier.Setup;

/// <summary>
/// A small window with a moving bar while the installer works (the first install, with the Windows App Runtime,
/// takes about a minute). The work runs on a separate STA thread (the Start menu shortcut is made through COM); the
/// window cannot be closed until it is done.
/// </summary>
internal static class ProgressWindow
{
    /// <summary>Runs <paramref name="work"/> while the window is shown. Returns what it threw, or null.</summary>
    public static Exception? Run(string title, string text, Action work)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Laid out in pixels at the system DPI (the manifest makes the process DPI aware); the font is in points.
        float scale;
        using (var screen = Graphics.FromHwnd(IntPtr.Zero)) scale = screen.DpiX / 96f;
        int Px(int value) => (int)Math.Round(value * scale);

        using var form = new Form
        {
            Text = title,
            Font = SystemFonts.MessageBoxFont,
            BackColor = SystemColors.Window, // like the message boxes before and after it

            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ControlBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
        };
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(Px(340), 0),
            Location = new Point(Px(20), Px(20)),
        };
        form.Controls.Add(label);
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(Px(20), label.Bottom + Px(16)),
            Size = new Size(Px(340), Px(16)),
        };
        form.Controls.Add(bar);
        form.ClientSize = new Size(Px(380), bar.Bottom + Px(24));

        Exception? failure = null;
        bool done = false;
        form.FormClosing += (_, e) => e.Cancel = !done;
        form.Shown += (_, _) =>
        {
            var worker = new Thread(() =>
            {
                failure = RunQuietly(work);
                form.BeginInvoke(new Action(() =>
                {
                    done = true;
                    form.Close();
                }));
            })
            { IsBackground = true, Name = "Install" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        };
        Application.Run(form);
        return failure;
    }

    /// <summary>Runs <paramref name="work"/> on this thread. Returns what it threw, or null.</summary>
    public static Exception? RunQuietly(Action work)
    {
        try
        {
            work();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
