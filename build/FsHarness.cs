using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using NostalgiaPlus;
using NostalgiaPlus.Audio;
using NostalgiaPlus.Ui;

// Drives the real FullscreenView offscreen, feeding it synthetic stereo through the
// capture ring so the mirrored layout can be inspected without live audio.
class FsHarness
{
    const double Sr = 48000;
    const int Fps = 60;

    static string[] Info()
    {
        return new string[] { "Maanaadu Theme", "Yuvan Shankar Raja", "Maanaadu (2021)" };
    }

    // Distinct material per channel so the mirror is obviously doing something:
    // left carries a low chord plus kick, right carries a higher chord plus hats,
    // and a shared sweep crosses both.
    static void Frame(double t0, float[] block, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            double t = t0 + i / Sr;
            double l = 0, r = 0;

            double[] lowChord = { 82.41, 110.0, 164.81 };   // E2 A2 E3
            for (int c = 0; c < lowChord.Length; c++)
                for (int k = 1; k <= 6; k++)
                    l += (0.15 / k) * Math.Sin(2 * Math.PI * lowChord[c] * k * t + c);

            double[] highChord = { 329.63, 493.88, 659.26 }; // E4 B4 E5
            for (int c = 0; c < highChord.Length; c++)
                for (int k = 1; k <= 5; k++)
                    r += (0.15 / k) * Math.Sin(2 * Math.PI * highChord[c] * k * t + c);

            double sweepT = (t % 8.0) / 8.0;
            double fs = 300 * Math.Pow(30.0, sweepT);
            double sweep = 0.10 * Math.Sin(2 * Math.PI * fs * t);
            l += sweep; r += sweep * 0.7;

            double kp = t % 0.5;
            if (kp < 0.16) l += 0.5 * Math.Exp(-kp * 24) * Math.Sin(2 * Math.PI * 52 * kp);

            double hp = t % 0.25;
            if (hp < 0.04)
            {
                var rnd = new Random(unchecked((int)(t * 4)) * 7919);
                r += 0.25 * Math.Exp(-hp * 100) * (rnd.NextDouble() * 2 - 1);
            }

            block[i * 2] = (float)(l * 0.5);
            block[i * 2 + 1] = (float)(r * 0.5);
        }
    }

    static void SetHover(object view, int x, int y)
    {
        var f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var t = view.GetType();
        var fx = t.GetField("_mouseX", f);
        var fy = t.GetField("_mouseY", f);
        var fin = t.GetField("_mouseIn", f);
        if (fx != null) fx.SetValue(view, x);
        if (fy != null) fy.SetValue(view, y);
        if (fin != null) fin.SetValue(view, true);
    }

    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        int W = 1920, H = 1080;

        var settings = new Settings();
        settings.ApplyPreset(Preset.Immersive);
        settings.MirrorLeftPane = true;   // verify the new centre-out arrangement
        settings.FsAutoHide = false;      // keep axes and readout visible for the capture
        settings.CurveWidthPct = 12;

        // Not started, so no WASAPI thread claims the endpoint - we own the ring.
        var cap = new LoopbackCapture();

        var view = new FullscreenView(settings, System.IO.Path.GetTempPath(), cap, Info);
        view.ShowAt(new Rectangle(-9000, -9000, W, H), false);

        int hop = (int)(Sr / Fps);
        var block = new float[hop * 2];
        double t = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        double nextFeed = 0;
        while (sw.Elapsed.TotalSeconds < 30.0)
        {
            double now = sw.Elapsed.TotalSeconds;
            while (nextFeed <= now)
            {
                Frame(t, block, hop);
                cap.Ring.Write(block, 0, hop, 2);
                t += hop / Sr;
                nextFeed += 1.0 / Fps;
            }
            Application.DoEvents();
            Thread.Sleep(2);
        }

        // Hover over the left pane so the synced readout is captured.
        SetHover(view, 520, 430);
        view.Invalidate();
        Application.DoEvents();
        Thread.Sleep(80);
        Application.DoEvents();

        using (var bmp = new Bitmap(W, H))
        {
            view.DrawToBitmap(bmp, new Rectangle(0, 0, W, H));
            bmp.Save(System.IO.Path.Combine(outDir, "fullscreen.png"), ImageFormat.Png);
        }
        Console.WriteLine("wrote fullscreen.png");

        view.Close();
        view.Dispose();

        RenderDockedPanel(outDir);
    }

    // Drives the real docked panel from the same synthetic stereo material.
    static void RenderDockedPanel(string outDir)
    {
        int W = 1400, H = 420;
        var settings = new Settings();
        settings.ApplyPreset(Preset.Studio);

        var cap = new LoopbackCapture();
        var form = new Form();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-9000, -9000);
        form.ShowInTaskbar = false;
        form.ClientSize = new Size(W, H);

        var panel = new AnalyzerPanel(settings, System.IO.Path.GetTempPath());
        panel.UseCapture(cap);
        panel.Dock = DockStyle.Fill;
        form.Controls.Add(panel);
        form.Show();
        panel.StartCapture();

        int hop = (int)(Sr / Fps);
        var block = new float[hop * 2];
        double t = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double nextFeed = 0;
        while (sw.Elapsed.TotalSeconds < 9.0)
        {
            double now = sw.Elapsed.TotalSeconds;
            while (nextFeed <= now)
            {
                Frame(t, block, hop);
                cap.Ring.Write(block, 0, hop, 2);
                t += hop / Sr;
                nextFeed += 1.0 / Fps;
            }
            Application.DoEvents();
            Thread.Sleep(2);
        }

        SetHover(panel, 380, 200);
        panel.Invalidate();
        Application.DoEvents();
        Thread.Sleep(80);
        Application.DoEvents();

        using (var bmp = new Bitmap(W, H))
        {
            panel.DrawToBitmap(bmp, new Rectangle(0, 0, W, H));
            bmp.Save(System.IO.Path.Combine(outDir, "panel-stereo.png"), ImageFormat.Png);
        }
        Console.WriteLine("wrote panel-stereo.png");
        panel.StopCapture();
        form.Close();
        form.Dispose();
    }
}
