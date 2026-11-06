// FlipClockShell.cs
// Minimal WinForms + WebBrowser shell that turns FlipClock.html into a real .scr.
// The HTML is embedded by the installer as resource "FlipClock.ClockHtml",
// so the compiled .scr is a single self-contained file in System32.
//
// Modes (standard Windows screensaver protocol):
//   /s          full screen on every monitor, exit on key / click / mouse move
//   /c          settings window (settings panel opens automatically)
//   /p <hwnd>   live preview inside the small monitor of the settings dialog
//   no args     same as /s
//
// Pure .NET Framework 2.0+ API on purpose: compiled with the csc.exe that ships
// with Windows, no NuGet, no SDK, no external DLL.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// JS bridge exposed as window.external inside the WebBrowser.
// Settings are stored in a real file instead of IE's file:// localStorage,
// so the /c settings window and the /s saver always share the same values.
[ComVisible(true)]
public class ClockBridge
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private static string ConfigPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlipClock");
            return Path.Combine(dir, "config.json");
        }
    }

    public string GetClockConfig()
    {
        try
        {
            string p = ConfigPath;
            return File.Exists(p) ? File.ReadAllText(p, Utf8) : "";
        }
        catch (Exception) { return ""; }
    }

    public void SetClockConfig(string json)
    {
        try
        {
            string p = ConfigPath;
            string dir = Path.GetDirectoryName(p);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, json == null ? "" : json, Utf8);
        }
        catch (Exception) { }
    }

    // Open the project page in the user's default browser instead of navigating
    // the WebBrowser control away from the settings document. Whitelist only:
    // this object is reachable from script, so never pass through arbitrary URLs.
    public void OpenProject(string url)
    {
        if (url != "https://github.com/xyzOvO/FlipClock" &&
            url != "https://github.com/xyzOvO/FlipClock.git") return;
        try
        {
            System.Diagnostics.ProcessStartInfo psi =
                new System.Diagnostics.ProcessStartInfo(url);
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception) { }
    }
}

internal static class FlipClockShell
{
    private const string ResourceName = "FlipClock.ClockHtml";
    // Mark-of-the-Web: without it IE blocks scripts on a local file:// page.
    private const string Motw = "<!-- saved from url=(0014)about:internet -->\r\n";
    private static string htmlPath;

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    // High-DPI: without this the process is DPI-unaware, Windows bitmap-scales the
    // whole window and Screen.Bounds reports virtualised coordinates, so the
    // WebBrowser content looks blurry and may not cover the whole monitor.
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [STAThread]
    private static void Main(string[] args)
    {
        // Must run before any window / WebBrowser is created. Vista+ only.
        try { SetProcessDPIAware(); } catch (Exception) { }

        string mode = "/s";
        string hwndArg = null;

        if (args.Length > 0)
        {
            mode = args[0].ToLowerInvariant();
            if (mode.StartsWith("/p:") && mode.Length > 3)
            {
                hwndArg = mode.Substring(3);
                mode = "/p";
            }
        }
        if (args.Length > 1) hwndArg = args[1];

        htmlPath = ExtractHtml();
        if (htmlPath == null) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (mode.StartsWith("/c")) { ShowSettings(); return; }
        if (mode.StartsWith("/p")) { try { ShowPreview(hwndArg); } catch (Exception) { } return; }
        ShowSaver();
    }

    // Write the embedded HTML to a stable temp path. A stable path matters: it is
    // the file:// origin used by the page, so the same document is used everywhere.
    private static string ExtractHtml()
    {
        string path = Path.Combine(Path.GetTempPath(), "FlipClock.html");
        try
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s == null) return null;

            byte[] data;
            using (s)
            using (MemoryStream mem = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) mem.Write(buf, 0, n);
                data = mem.ToArray();
            }

            // Keep the MOTW even if a replaced HTML forgot it.
            byte[] mark = Encoding.ASCII.GetBytes(Motw);
            string head = Encoding.ASCII.GetString(data, 0, Math.Min(96, data.Length));
            byte[] final;
            if (head.IndexOf("saved from url=", StringComparison.Ordinal) < 0)
            {
                final = new byte[mark.Length + data.Length];
                Buffer.BlockCopy(mark, 0, final, 0, mark.Length);
                Buffer.BlockCopy(data, 0, final, mark.Length, data.Length);
            }
            else final = data;

            // Always compare with the embedded bytes: reinstalling a newer .scr
            // must replace an older cached copy in the user's temp folder.
            try
            {
                if (File.Exists(path))
                {
                    byte[] old = File.ReadAllBytes(path);
                    if (old.Length == final.Length)
                    {
                        bool same = true;
                        for (int i = 0; i < old.Length; i++)
                            if (old[i] != final[i]) { same = false; break; }
                        if (same) return path;
                    }
                }
            }
            catch (Exception) { }

            File.WriteAllBytes(path, final);
            return path;
        }
        catch (Exception) { return null; }
    }

    private static WebBrowser MakeBrowser(string tag)
    {
        WebBrowser wb = new WebBrowser();
        wb.Dock = DockStyle.Fill;
        wb.ScrollBarsEnabled = false;
        wb.ScriptErrorsSuppressed = true;
        wb.IsWebBrowserContextMenuEnabled = false;
        wb.AllowWebBrowserDrop = false;
        wb.ObjectForScripting = new ClockBridge();

        bool applied = false;
        wb.DocumentCompleted += delegate(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            if (applied) return;
            applied = true;
            try { wb.Document.InvokeScript("ApplyShellMode", new object[] { tag }); }
            catch (Exception) { }
        };

        wb.Url = new Uri(htmlPath);
        return wb;
    }

    private static Form MakeSaverForm(Rectangle bounds, string tag)
    {
        Form f = new Form();
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.Manual;
        f.Bounds = bounds;
        f.BackColor = Color.Black;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.Controls.Add(MakeBrowser(tag));
        return f;
    }

    private static void ShowSaver()
    {
        // WebBrowser swallows Form mouse/key events, so filter at message level.
        Application.AddMessageFilter(new ExitFilter());
        Screen[] screens = Screen.AllScreens;
        Form main = null;
        for (int i = 0; i < screens.Length; i++)
        {
            Form f = MakeSaverForm(screens[i].Bounds, "s");
            if (i == 0) main = f; else f.Show();
        }
        Cursor.Hide();
        try
        {
            if (main != null) Application.Run(main);
        }
        finally { Cursor.Show(); }
    }

    private static void ShowSettings()
    {
        Form f = new Form();
        f.Text = "翻页时钟 · 设置（改动自动保存，关闭本窗口即可）";
        f.Size = new Size(1000, 700);
        f.MinimizeBox = false;
        f.MaximizeBox = false;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.BackColor = Color.Black;
        f.Controls.Add(MakeBrowser("c"));
        Application.Run(f);
    }

    private static void ShowPreview(string hwndArg)
    {
        string digits = "";
        if (hwndArg != null)
        {
            for (int i = 0; i < hwndArg.Length; i++)
                if (char.IsDigit(hwndArg[i])) digits += hwndArg[i];
        }
        if (digits.Length == 0) return;
        long h;
        if (!long.TryParse(digits, out h) || h == 0) return;

        IntPtr parent = new IntPtr(unchecked((int)h));
        // The /p handle is the small preview control in the Settings dialog.
        // Use its real client size instead of a hard-coded 320x200, otherwise
        // the preview is clipped or leaves a black border on scaled displays.
        int w = 320, ht = 200;
        RECT rc;
        if (GetClientRect(parent, out rc) && rc.Right > rc.Left && rc.Bottom > rc.Top)
        {
            w = rc.Right - rc.Left;
            ht = rc.Bottom - rc.Top;
        }

        Form f = new Form();
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.Manual;
        f.Bounds = new Rectangle(0, 0, w, ht);
        f.BackColor = Color.Black;
        f.Controls.Add(MakeBrowser("p"));
        f.Show();
        SetParent(f.Handle, parent);
        Application.Run(f);
    }

    private sealed class ExitFilter : IMessageFilter
    {
        private int lastX = -1;
        private int lastY = -1;

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEMOVE = 0x0200;
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;

            if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN ||
                m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN)
            {
                Application.Exit();
                return false;
            }
            if (m.Msg == WM_MOUSEMOVE)
            {
                Point p = Cursor.Position;
                if (lastX < 0) { lastX = p.X; lastY = p.Y; return false; }
                if (Math.Abs(p.X - lastX) > 10 || Math.Abs(p.Y - lastY) > 10)
                {
                    Application.Exit();
                    return false;
                }
            }
            return false;
        }
    }
}
