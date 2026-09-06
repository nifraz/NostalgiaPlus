using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Mimics how MusicBee actually loads a plugin: find MusicBeePlugin.Plugin by name,
// then drive the documented lifecycle. Catches namespace, signature and struct-layout
// mistakes that a plain compile will not.
class Verify
{
    static int _fail = 0;

    static void Check(string label, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label + (detail.Length > 0 ? "   " + detail : ""));
        if (!ok) _fail++;
    }

    [STAThread]
    static int Main(string[] args)
    {
        string dll = args[0];
        string outDir = args.Length > 1 ? args[1] : ".";
        Console.WriteLine("=== MusicBee plugin load simulation ===\n");
        Console.WriteLine("target: " + dll + "\n");

        Assembly asm = Assembly.LoadFrom(dll);

        // 1. discovery, exactly as the host does it
        Type t = asm.GetType("MusicBeePlugin.Plugin");
        Check("MusicBeePlugin.Plugin type resolves", t != null, t == null ? "NOT FOUND" : t.FullName);
        if (t == null) { Console.WriteLine("\nFAILED"); return 1; }

        // 2. required entry points
        var required = new[] {
            "Initialise", "Close", "Configure", "SaveSettings",
            "Uninstall", "ReceiveNotification", "OnDockablePanelCreated"
        };
        foreach (string name in required)
        {
            MethodInfo mi = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Check("entry point " + name, mi != null, mi == null ? "MISSING" :
                  mi.ReturnType.Name + "(" + string.Join(", ",
                      mi.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")");
        }

        object plugin = Activator.CreateInstance(t);
        Check("plugin instantiates", plugin != null, "");

        // 3. Initialise with a zeroed API block - exercises the real struct marshaling.
        Type apiType = t.GetNestedType("MusicBeeApiInterface");
        Check("MusicBeeApiInterface nested struct present", apiType != null, "");
        if (apiType == null) return 1;

        int size = Marshal.SizeOf(apiType);
        Console.WriteLine("\n  api struct marshals to " + size + " bytes (" +
                          (size / IntPtr.Size) + " pointer slots)\n");

        IntPtr block = Marshal.AllocHGlobal(size);
        object info = null;
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(block, i, 0);
            MethodInfo init = t.GetMethod("Initialise");
            info = init.Invoke(plugin, new object[] { block });
            Check("Initialise() survives struct marshaling", info != null, "");
        }
        catch (Exception ex)
        {
            Check("Initialise() survives struct marshaling", false,
                  (ex.InnerException ?? ex).Message);
            Marshal.FreeHGlobal(block);
            return 1;
        }

        if (info != null)
        {
            Type it = info.GetType();
            Func<string, object> f = n => it.GetField(n).GetValue(info);
            Console.WriteLine("  PluginInfo:");
            Console.WriteLine("    Name        " + f("Name"));
            Console.WriteLine("    Type        " + f("Type"));
            Console.WriteLine("    Version     " + f("VersionMajor") + "." + f("VersionMinor") + "." + f("Revision"));
            Console.WriteLine("    Notify      " + f("ReceiveNotifications"));
            Console.WriteLine();
            Check("declares PanelView type", f("Type").ToString() == "PanelView", f("Type").ToString());
            Check("has a name", !string.IsNullOrEmpty((string)f("Name")), (string)f("Name"));
        }

        // 4. panel lifecycle against a real host control
        try
        {
            var form = new Form();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-6000, -6000);
            form.ShowInTaskbar = false;
            form.ClientSize = new Size(1420, 700);

            var host = new Panel();
            host.Dock = DockStyle.Fill;
            form.Controls.Add(host);
            form.Show();

            // MusicBee calls this from a thread other than the one owning the host
            // panel. Calling it inline would hide exactly the cross-thread parenting
            // bug that stopped the panel appearing, so reproduce the host's behaviour.
            MethodInfo created = t.GetMethod("OnDockablePanelCreated");
            object rc = null;
            Exception callErr = null;
            var done = new ManualResetEvent(false);
            var worker = new Thread(delegate ()
            {
                try { rc = created.Invoke(plugin, new object[] { host }); }
                catch (Exception ex) { callErr = ex.InnerException ?? ex; }
                done.Set();
            });
            worker.IsBackground = true;
            worker.Start();
            // Pump messages so the plugin's marshalling onto the UI thread can complete.
            var guard = System.Diagnostics.Stopwatch.StartNew();
            while (!done.WaitOne(0) && guard.Elapsed.TotalSeconds < 10)
            { Application.DoEvents(); Thread.Sleep(5); }

            Check("OnDockablePanelCreated() from a background thread", callErr == null,
                  callErr == null ? "returned " + rc : callErr.Message);
            Check("panel added a child control", host.Controls.Count == 1,
                  host.Controls.Count + " child control(s)");
            if (host.Controls.Count == 1)
                Check("child control belongs to the UI thread", !host.Controls[0].InvokeRequired,
                      "InvokeRequired=" + host.Controls[0].InvokeRequired);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 3.0) { Application.DoEvents(); Thread.Sleep(10); }

            using (var bmp = new Bitmap(host.Width, host.Height))
            {
                host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height));
                bmp.Save(System.IO.Path.Combine(outDir, "verify-panel.png"), ImageFormat.Png);
            }
            Check("panel painted without throwing", true, "wrote verify-panel.png");

            // 5. shutdown
            t.GetMethod("SaveSettings").Invoke(plugin, null);
            Check("SaveSettings() runs", true, "");

            object reason = Enum.Parse(asm.GetType("MusicBeePlugin.PluginCloseReason"), "MusicBeeClosing");
            t.GetMethod("Close").Invoke(plugin, new object[] { reason });
            Check("Close() runs", true, "");

            form.Close();
            form.Dispose();
        }
        catch (Exception ex)
        {
            Check("panel lifecycle", false, (ex.InnerException ?? ex).ToString());
        }
        finally { Marshal.FreeHGlobal(block); }

        Console.WriteLine(_fail == 0 ? "\nPLUGIN WILL LOAD" : "\n" + _fail + " PROBLEM(S)");
        return _fail == 0 ? 0 : 1;
    }
}
