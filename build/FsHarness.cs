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

    // The views smooth their own paint/analysis timings into private fields; read
    // them back so the harness can report cost instead of us guessing at it.
    // Lets the harness isolate the cost of individual draw stages.
    static string[] Flags = new string[0];
    static bool Flag(string name)
    {
        foreach (var f in Flags) if (f == name) return true;
        return false;
    }

    static void ApplyFlags(Settings s)
    {
        if (Flag("noglow")) s.FsGlow = false;
        if (Flag("nowave")) s.FsShowWaveform = false;
        if (Flag("nogrid")) s.ShowGrid = false;
        if (Flag("nooverlay")) s.FsShowOverlays = false;
        if (Flag("nohover")) s.ShowHud = false;
        if (Flag("notrace")) { s.ShowMax = false; s.ShowMin = false; s.ShowAvg = false; }
    }

    static void ReportTiming(object view, string label)
    {
        var f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var t = view.GetType();
        string[] names = { "_fps", "_analysisMs", "_paintMs" };
        string line = label + ":";
        foreach (var n in names)
        {
            var fi = t.GetField(n, f);
            if (fi == null) continue;
            double v = Convert.ToDouble(fi.GetValue(view));
            line += "  " + n.TrimStart('_') + "=" + v.ToString("0.00");
        }
        Console.WriteLine(line);
    }

    // Both views expose pointer state through a private HoverInfo; the harness has no
    // real cursor to move, so poke a position (and optionally a drag) straight in.
    static void SetHover(object view, int x, int y, int ox, int oy)
    {
        var f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var fi = view.GetType().GetField("_hover", f);
        if (fi == null) return;
        object h = fi.GetValue(view);
        var ht = h.GetType();
        ht.GetField("Cursor").SetValue(h, new Point(x, y));
        ht.GetField("Active").SetValue(h, true);
        if (ox >= 0)
        {
            ht.GetField("Origin").SetValue(h, new Point(ox, oy));
            ht.GetField("Measuring").SetValue(h, true);
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        if (args.Length > 1)
        {
            Flags = new string[args.Length - 1];
            Array.Copy(args, 1, Flags, 0, args.Length - 1);
        }
        int W = 1920, H = 1080;

        var settings = new Settings();
        settings.ApplyPreset(Preset.Immersive);
        settings.MirrorLeftPane = true;   // verify the new centre-out arrangement
        settings.FsAutoHide = false;      // keep axes and readout visible for the capture
        settings.CurveWidthPct = 12;
        settings.ShowHarmonics = false;
        settings.LabelMode = AxisLabelMode.Notes;
        settings.LabelFontSize = 7f;
        ApplyFlags(settings);

        // Not started, so no WASAPI thread claims the endpoint - we own the ring.
        var cap = new LoopbackCapture();

        var view = new FullscreenView(settings, System.IO.Path.GetTempPath(), cap, Info);
        view.ShowAt(new Rectangle(-9000, -9000, W, H), false);

        int hop = (int)(Sr / Fps);
        var block = new float[hop * 2];
        double t = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        double nextFeed = 0;
        while (sw.Elapsed.TotalSeconds < (Flags.Length > 0 ? 12.0 : 30.0))
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
        SetHover(view, 520, 430, 300, 620);
        view.Invalidate();
        Application.DoEvents();
        Thread.Sleep(80);
        Application.DoEvents();

        using (var bmp = new Bitmap(W, H))
        {
            view.DrawToBitmap(bmp, new Rectangle(0, 0, W, H));
            bmp.Save(System.IO.Path.Combine(outDir, "fullscreen.png"), ImageFormat.Png);
        }
        ReportTiming(view, "fullscreen");
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

        SetHover(panel, 380, 200, -1, -1);
        panel.Invalidate();
        Application.DoEvents();
        Thread.Sleep(80);
        Application.DoEvents();

        using (var bmp = new Bitmap(W, H))
        {
            panel.DrawToBitmap(bmp, new Rectangle(0, 0, W, H));
            bmp.Save(System.IO.Path.Combine(outDir, "panel-stereo.png"), ImageFormat.Png);
        }
        ReportTiming(panel, "docked    ");
        Console.WriteLine("wrote panel-stereo.png");
        panel.StopCapture();
        form.Close();
        form.Dispose();
    }
}
