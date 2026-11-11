using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Ostrovok;

/// <summary>
/// A borderless, always-on-top, never-activating window at the top centre of the primary screen.
/// The island itself is drawn by a transparent WebView2 page (ui/index.html); this class feeds it
/// live system data and carries out what the user does in it.
/// Only the island's current rectangle is clickable (window region), so the rest of the screen works as usual.
/// </summary>
sealed class IslandForm : Form
{
    // The window spans the top of the primary screen so cells can split off to either side or below;
    // only the island and its cells are clickable (window region), everything else passes through.
    const int MaxCssHeight = 820;

    readonly WebView2 web;
    readonly MediaWatcher media = new();
    readonly AudioController audio = new();
    readonly RadioController radios = new();
    readonly SysMonitor sys = new();
    readonly WindowTracker windows = new();
    readonly SearchHub search = new();
    readonly BtHub bt = new();
    readonly ProcMonitor procs = new();
    readonly WeatherHub weather = new();
    readonly AppSettings settings = AppSettings.Load();
    readonly TelegramWatcher telegram = new();
    int lastUnread = -2;
    bool telegramBusy;
    AudioHub audioHub;
    WifiHub wifi;
    readonly HashSet<string> watching = new();
    readonly System.Windows.Forms.Timer meterTimer = new() { Interval = 50 };
    readonly System.Windows.Forms.Timer slowTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer cursorTimer = new() { Interval = 200 };
    // screen recording and screenshots: a blinking red dot / a camera in the island
    readonly CaptureWatch capture = new();
    bool capRec, capCam;
    DateTime camUntil;
    NotifyIcon tray;
    Native.WinEventProc fgProc;
    IntPtr fgHook, previousForeground;

    bool ready, hiddenForFullscreen, cursorInside, procsBusy, btBusy;
    float scale = 1f;
    int cssWidth = 1920, cssHeight = 600;
    List<Rectangle> regionCss = new();
    List<AppEntry> favorites = new();
    List<WinInfo> lastWindows = new();
    string lastWindowsKey = "", lastSysKey = "";
    int tick;

    public IslandForm()
    {
        Text = "Островок";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;

        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
        Controls.Add(web);

        meterTimer.Tick += (_, _) => PushLevel();
        slowTimer.Tick += (_, _) => SlowTick();
        cursorTimer.Tick += (_, _) => CursorTick();
        SystemEvents.DisplaySettingsChanged += (_, _) => BeginInvoke(Place);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_NOREDIRECTIONBITMAP);
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    void Place()
    {
        scale = DeviceDpi / 96f;
        var screen = Screen.PrimaryScreen.Bounds;
        int h = Math.Min((int)Math.Round(MaxCssHeight * scale), screen.Height - (int)(60 * scale));
        Bounds = new Rectangle(screen.X, screen.Y, screen.Width, h);
        cssWidth = (int)(screen.Width / scale); cssHeight = (int)(h / scale);
        if (regionCss.Count == 0) regionCss.Add(new Rectangle((cssWidth - 340) / 2, 0, 340, 56));
        ApplyRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Place();
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    void ApplyRegion()
    {
        if (!IsHandleCreated || regionCss.Count == 0) return;
        IntPtr Make(Rectangle r) => Native.CreateRectRgn((int)Math.Floor(r.X * scale), (int)Math.Floor(r.Y * scale),
                                                         (int)Math.Ceiling(r.Right * scale), (int)Math.Ceiling(r.Bottom * scale));
        var rgn = Make(regionCss[0]);
        foreach (var r in regionCss.Skip(1))
        {
            var part = Make(r);
            CombineRgn(rgn, rgn, part, 2 /* RGN_OR */);
            DeleteObject(part);
        }
        Native.SetWindowRgn(Handle, rgn, true); // the system owns rgn from here on
    }

    /// <summary>Let the island take the keyboard (search box, passwords), remembering who had it.</summary>
    void TakeFocus(bool on, bool restore = true)
    {
        if (!on && !restore) { previousForeground = IntPtr.Zero; return; }
        try
        {
            var fg = Native.GetForegroundWindow();
            if (on)
            {
                if (fg != Handle) previousForeground = fg;
                uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = Native.GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
                Native.SetForegroundWindow(Handle);
                if (attached) Native.AttachThreadInput(me, fgThread, false);
                web.Focus();
            }
            else if (fg == Handle && previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground))
            {
                Native.SetForegroundWindow(previousForeground);
            }
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Place();
        windows.Self = Handle;
        CreateTray();

        try
        {
            var dataDir = Path.Combine(Log.Dir, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir,
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            var st = core.Settings;
            st.AreDefaultContextMenusEnabled = false;
            st.AreDevToolsEnabled = Debugger.IsAttached;
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            core.SetVirtualHostNameToFolderMapping("ostrovok.local", Path.Combine(AppContext.BaseDirectory, "ui"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://ostrovok.local/index.html");
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            MessageBox.Show("Островку не удалось запустить WebView2:\n" + ex.Message, "Островок");
            Close();
            return;
        }

        try { audio.Init(); audio.Changed += (lv, mute, ext) => BeginInvoke(() => PushVolume(lv, mute, ext)); }
        catch (Exception ex) { Log.Write(ex); }

        try { audioHub = new AudioHub(); } catch (Exception ex) { Log.Write(ex); }
        try { wifi = new WifiHub(); } catch (Exception ex) { Log.Write(ex); }

        media.Changed += () => BeginInvoke(PushMedia);
        media.TimelineChanged += () => BeginInvoke(PushMedia);
        radios.Changed += () => BeginInvoke(PushRadios);
        sys.Start();
        _ = InitAsync();
        BeginInvoke(async () => { await Task.Delay(6000); search.EnsureApps(); });   // after the start-up slide: indexing holds this thread

        fgProc = (_, _, _, _, _, _, _) => BeginInvoke(PushWindows);
        fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        meterTimer.Start();
        slowTimer.Start();
        cursorTimer.Start();
    }

    async Task InitAsync()
    {
        try { await media.InitAsync(); } catch (Exception ex) { Log.Write("media init: " + ex.Message); }
        try { await radios.InitAsync(); } catch (Exception ex) { Log.Write("radio init: " + ex.Message); }
    }

    // ───────────── page → host ─────────────

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "ready":
                    ready = true;
                    PushAll();
                    break;
                case "log":
                    Log.Write("page: " + m.GetProperty("msg").GetString());
                    break;
                case "mode":
                    settings.Mode = m.GetProperty("mode").GetString();
                    settings.Save();
                    break;
                case "tg.open":
                    telegram.Open();
                    break;
                case "rect":
                    regionCss = m.GetProperty("rects").EnumerateArray()
                        .Select(r => new Rectangle(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
                                                   r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32())).ToList();
                    ApplyRegion();
                    break;
                case "launch":
                    {
                        var id = m.GetProperty("id").GetString();
                        var fav = favorites.FirstOrDefault(f => f.Target == id) ?? new AppEntry { Target = id };
                        Favorites.Launch(fav);
                        break;
                    }
                case "open":
                    Shell.Open(m.GetProperty("id").GetString());
                    break;
                case "focus":
                    TakeFocus(m.GetProperty("on").GetBoolean(), !m.TryGetProperty("restore", out var rs) || rs.GetBoolean());
                    break;
                case "search":
                    _ = SearchAsync(m.GetProperty("q").GetString() ?? "", m.GetProperty("seq").GetInt32(), m.TryGetProperty("appsOnly", out var ao) && ao.GetBoolean());
                    break;
                case "favs.save":
                    Favorites.Save(m.GetProperty("list").EnumerateArray().Select(x => x.GetString()));
                    favorites = Favorites.Load();
                    PushFavorites();
                    break;
                case "watch":
                    {
                        var what = m.GetProperty("what").GetString();
                        if (m.GetProperty("on").GetBoolean()) { watching.Add(what); PushWatched(what, true); }
                        else watching.Remove(what);
                        break;
                    }
                case "audio.out":
                case "audio.in":
                    audioHub?.SetDefault(m.GetProperty("id").GetString());
                    PushWatched("audio", true);
                    BeginInvoke(() => PushVolume(audio.Level, audio.Muted, false));
                    break;
                case "audio.app":
                    audioHub?.SetAppVolume(m.GetProperty("id").GetString(), (float)m.GetProperty("v").GetDouble());
                    break;
                case "audio.appMute":
                    audioHub?.ToggleAppMute(m.GetProperty("id").GetString());
                    PushWatched("audio", true);
                    break;
                case "audio.mic":
                    audioHub?.SetMicLevel((float)m.GetProperty("v").GetDouble());
                    break;
                case "audio.micMute":
                    audioHub?.ToggleMicMute();
                    PushWatched("audio", true);
                    break;
                case "bt.device":
                    {
                        var id = m.GetProperty("id").GetString();
                        bool connect = m.GetProperty("connect").GetBoolean();
                        _ = Task.Run(() => bt.Toggle(id, connect)).ContinueWith(t =>
                        {
                            if (!t.Result) BeginInvoke(() => Shell.Open("ms-settings:bluetooth"));
                            BeginInvoke(async () => { await Task.Delay(2500); PushWatched("bt", true); });
                        });
                        break;
                    }
                case "wifi.connect":
                    {
                        var ssid = m.GetProperty("ssid").GetString();
                        var pass = m.TryGetProperty("password", out var pw) ? pw.GetString() : null;
                        bool ok = wifi?.Connect(ssid, pass) ?? false;
                        Post(new { t = "wifi.result", ssid, ok });
                        BeginInvoke(async () => { await Task.Delay(3000); PushWatched("wifi", true); });
                        break;
                    }
                case "wifi.disconnect":
                    wifi?.Disconnect();
                    BeginInvoke(async () => { await Task.Delay(1500); PushWatched("wifi", true); });
                    break;
                case "weather.city":
                    settings.City = m.GetProperty("city").GetString()?.Trim();
                    settings.Save();
                    PushWatched("weather", true);
                    break;
                case "activate":
                    WindowTracker.Activate(new IntPtr(long.Parse(m.GetProperty("id").GetString())));
                    BeginInvoke(PushWindows);
                    break;
                case "close":
                    WindowTracker.Close(new IntPtr(long.Parse(m.GetProperty("id").GetString())));
                    break;
                case "closeMedia":
                    _ = CloseMediaAppAsync();
                    break;
                case "cmd":
                    Command(m.GetProperty("c").GetString(), m.TryGetProperty("v", out var v) ? v.GetDouble() : 0);
                    break;
            }
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    void Command(string c, double v)
    {
        switch (c)
        {
            case "play" or "next" or "prev" or "seek": _ = media.Command(c, v); break;
            case "taskmgr": Shell.Open("taskmgr.exe"); break;
            case "netsettings": Shell.Open("ms-settings:network-status"); break;
            case "btsettings": Shell.Open("ms-settings:bluetooth"); break;
            case "wifisettings": Shell.Open("ms-settings:network-wifi"); break;
            case "location": Shell.Open("ms-settings:privacy-location"); break;
            case "vol": audio.SetLevel((float)v); break;
            case "mute": audio.SetMuted(!audio.Muted); PushVolume(audio.Level, audio.Muted, false); break;
            case "wifi" or "bt": _ = radios.ToggleAsync(c); break;
            case "start": Native.PressKeys(Native.VK_LWIN); break;
            case "search": Native.PressKeys(Native.VK_LWIN, Native.VK_S); break;
            case "settings": Shell.Open("ms-settings:"); break;
            case "sleep": Native.SetSuspendState(false, false, false); break;
            case "restart": Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); break;
            case "shutdown": Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); break;
        }
    }

    // ───────────── host → page ─────────────

    void Post(object message)
    {
        if (!ready || web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) { Log.Write(ex); }
    }

    void PushFavorites() =>
        Post(new { t = "favs", list = favorites.Select(f => new { id = f.Target, name = f.Name, icon = f.Icon }) });

    void PushAll()
    {
        favorites = Favorites.Load();
        PushFavorites();
        Post(new { t = "config", wifi = wifi?.Available ?? false, city = settings.City, idleMinutes = settings.IdleMinutes, mode = settings.Mode });
        lastWindowsKey = "";
        PushWindows();
        PushMedia();
        PushVolume(audio.Level, audio.Muted, false);
        PushRadios();
        lastSysKey = "";
        PushSys();
        lastUnread = -2;
        PollTelegram();
        PushNet();
        CheckCapture(true, true);
    }

    /// <summary>
    /// The camera shows while the Snipping Tool overlay is up and a moment after any screenshot;
    /// the red dot while something records or shares the screen.
    /// </summary>
    void CheckCapture(bool poll, bool force = false)
    {
        try
        {
            if (poll)
            {
                capture.RefreshSnipPids();
                var (rec, shot) = capture.Poll();
                capRec = rec;
                if (shot) camUntil = DateTime.Now.AddSeconds(1.6);
            }
            bool snip = CaptureWatch.SnipOverlay();
            if (snip)
            {
                camUntil = DateTime.Now.AddSeconds(1.2);
                // the overlay opens above every window; step back in front of it so the camera is seen
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
            bool cam = DateTime.Now < camUntil;
            // Snipping Tool bars at the top centre: the area picker (inside its full-screen overlay) or the recording controls
            int bar = capture.TopBarBottom(Screen.PrimaryScreen.Bounds.Width);
            int avoid = snip ? 70 : bar > 0 ? (int)Math.Ceiling(bar / scale) + 6 : 0;
            if (cam != capCam || snip != snipSeen || avoid != avoidSent || poll && capRec != lastRecSent || force)
            {
                capCam = cam; snipSeen = snip; avoidSent = avoid; lastRecSent = capRec;
                Post(new { t = "capture", rec = capRec, cam, snip, avoid });
            }
        }
        catch (Exception ex) { Log.Write("capture: " + ex.Message); }
    }
    bool lastRecSent, snipSeen;
    int avoidSent;

    /// <summary>Internet: online or not, and whether over Wi-Fi or cable.</summary>
    void PushNet()
    {
        try
        {
            var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            bool online = p != null && p.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
            string kind = p != null && p.IsWlanConnectionProfile ? "wifi" : "ethernet";
            Post(new { t = "net", online, kind, name = p?.ProfileName });
        }
        catch (Exception ex) { Log.Write("net: " + ex.Message); }
    }

    /// <summary>Unread counter every second, and any new Telegram toasts from Windows' notification history.</summary>
    void PollTelegram()
    {
        int n = telegram.ReadUnread();
        if (n != lastUnread) { lastUnread = n; Post(new { t = "tgUnread", n }); }
        if (telegramBusy) return;
        telegramBusy = true;
        _ = Task.Run(() => telegram.ReadNew()).ContinueWith(t =>
        {
            telegramBusy = false;
            if (!t.IsCompletedSuccessfully || t.Result.Count == 0) return;
            Log.Write($"telegram: {t.Result.Count} new notification(s), avatar: {t.Result.Count(x => x.avatar != null)}"); // never the text
            BeginInvoke(() => { foreach (var msg in t.Result) Post(new { t = "tg", msg.name, msg.text, msg.avatar }); });
        });
    }

    void PushWindows()
    {
        try
        {
            lastWindows = windows.Scan();
            var fg = Native.GetForegroundWindow();
            var key = string.Join("|", lastWindows.Select(w => $"{w.Handle}:{w.Title}")) + "#" + fg;
            if (key == lastWindowsKey) return;
            lastWindowsKey = key;
            Post(new
            {
                t = "wins",
                list = lastWindows.Select(w => new { id = w.Handle.ToString(), name = w.Name, title = w.Title, icon = w.Icon, active = w.Handle == fg })
            });
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    void PushMedia()
    {
        var owner = MediaOwner();
        var (pos, dur) = media.Timeline();
        Post(new
        {
            t = "media", has = media.Has, playing = media.Playing, title = media.Title, artist = media.Artist,
            art = media.Art, canNext = media.CanNext, canPrev = media.CanPrev, canSeek = media.CanSeek,
            app = owner.Name, browser = owner.Browser, pos = Math.Round(pos, 1), dur = Math.Round(dur, 1)
        });
    }

    // ───────────── cells: search and the live panels ─────────────

    async Task SearchAsync(string q, int seq, bool appsOnly)
    {
        q = q.Trim().ToLowerInvariant();
        search.EnsureApps();
        if (q.Length == 0)
        {
            var recent = SearchHub.RecentFiles(6);
            Post(new
            {
                t = "search.res", seq, empty = true,
                apps = favorites.Take(8).Select(f => new { id = f.Target, name = f.Name, icon = f.Icon }),
                settings = Array.Empty<object>(),
                files = recent.Select(f => new { id = f.lnk, name = f.name, sub = Ago(f.when), icon = ShellIcons.Icon(f.lnk, 48) })
            });
            return;
        }
        var apps = search.FindApps(q, appsOnly ? 8 : 5).Select(a => new { id = a.id, name = a.name, icon = ShellIcons.Icon(a.id) }).ToList();
        var sets = appsOnly ? null : search.FindSettings(q);
        Post(new { t = "search.res", seq, apps, settings = sets, files = Array.Empty<object>(), pending = !appsOnly });
        if (appsOnly) return;
        var files = await Task.Run(() => SearchHub.FindFiles(q, 4));
        Post(new
        {
            t = "search.files", seq,
            files = files.Select(f => new { id = f.path, name = f.name, sub = f.folder, icon = ShellIcons.Icon(f.path, 48) })
        });
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now.Date - t.Date;
        return d.TotalDays < 1 ? "сегодня, " + t.ToString("HH:mm") : d.TotalDays < 2 ? "вчера" : t.ToString("d MMMM");
    }

    /// <summary>Sends the data a cell is showing. Called when it opens, and every second while it stays open.</summary>
    void PushWatched(string what, bool force)
    {
        try
        {
            switch (what)
            {
                case "audio":
                    if (audioHub != null) Post(audioHub.Snapshot());
                    break;
                case "bt":
                    if (btBusy) break;
                    btBusy = true;
                    _ = Task.Run(() => bt.ListAsync()).ContinueWith(t =>
                    {
                        btBusy = false;
                        if (t.IsCompletedSuccessfully) BeginInvoke(() => Post(new { t = "bt", on = radios.BtOn, devices = t.Result }));
                    });
                    break;
                case "wifi":
                    if (wifi == null) break;
                    if (force) wifi.Scan();
                    var (list, error) = wifi.Networks();
                    Post(new { t = "wifi", on = radios.WifiOn, networks = list, error });
                    break;
                case "procs":
                    if (procsBusy) break;
                    procsBusy = true;
                    _ = Task.Run(() => procs.Sample()).ContinueWith(t =>
                    {
                        procsBusy = false;
                        if (!t.IsCompletedSuccessfully) return;
                        var (rows, used, total) = t.Result;
                        BeginInvoke(() => Post(new
                        {
                            t = "procs", load = (int)Math.Round(sys.Load), temp = sys.Temp is double d ? (int?)Math.Round(d) : null,
                            gpu = sys.Gpu is double g ? (int?)Math.Round(g) : null,
                            used = Math.Round(used, 1), total = Math.Round(total, 1),
                            list = rows.Select(r => new { name = r.name, icon = r.exe != null ? ShellIcons.Icon(r.exe) : null, cpu = r.cpu, mem = r.memMb })
                        }));
                    });
                    break;
                case "weather":
                    if (!force) break;
                    _ = weather.GetAsync(settings.City).ContinueWith(t => { if (t.IsCompletedSuccessfully) BeginInvoke(() => Post(t.Result)); });
                    break;
            }
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    // ───────────── the app that is playing music ─────────────

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "browser", "brave", "vivaldi", "yandex", "arc" };
    // never killed, whatever the media session claims
    static readonly string[] Protected = { "explorer", "applicationframehost", "svchost", "dwm", "ostrovok", "msedgewebview2", "runtimebroker" };

    sealed record MusicApp(List<WinInfo> Windows, bool Browser, string Name, string Token);

    MusicApp MediaOwner()
    {
        var src = media.SourceApp;
        if (string.IsNullOrEmpty(src)) return new MusicApp(new(), false, null, null);
        string token = src.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(src) : src;

        bool Matches(WinInfo w)
        {
            if (w.Aumid != null && w.Aumid.Equals(src, StringComparison.OrdinalIgnoreCase)) return true;
            if (w.Exe == null || w.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var exe = Path.GetFileNameWithoutExtension(w.Exe);
            return exe.Equals(token, StringComparison.OrdinalIgnoreCase)
                || (exe.Length >= 4 && src.Contains(exe, StringComparison.OrdinalIgnoreCase));
        }

        var wins = lastWindows.Where(Matches).ToList();
        bool browser = Browsers.Any(b => token.Equals(b, StringComparison.OrdinalIgnoreCase) || token.StartsWith(b + ".", StringComparison.OrdinalIgnoreCase))
                    || wins.Any(w => Browsers.Contains(Path.GetFileNameWithoutExtension(w.Exe ?? "").ToLowerInvariant()));
        string name = wins.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(name) && src.Contains('!')) name = ShellIcons.DisplayName("shell:AppsFolder\\" + src);
        if (string.IsNullOrWhiteSpace(name) && token.Contains('.') && !token.Contains(' '))
        {
            // ids like "ru.yandex.desktop.music": find a window whose program path mentions a distinctive part of it (display only)
            var parts = token.Split('.').Where(p => p.Length >= 5 && p is not ("desktop" or "windows" or "client")).ToList();
            name = lastWindows.Where(w => w.Exe != null && !Browsers.Contains(Path.GetFileNameWithoutExtension(w.Exe).ToLowerInvariant()))
                              .FirstOrDefault(w => parts.Any(p => w.Exe.Contains(p, StringComparison.OrdinalIgnoreCase)))?.Name;
        }
        if (string.IsNullOrWhiteSpace(name)) name = token;
        return new MusicApp(wins, browser, name, token);
    }

    /// <summary>
    /// The player was thrown into the trash: close the program that plays the music.
    /// A browser is never closed (that would close every tab) — the music is paused instead.
    /// </summary>
    async Task CloseMediaAppAsync()
    {
        try
        {
            lastWindows = windows.Scan();
            var owner = MediaOwner();
            if (owner.Token == null) return;
            if (owner.Browser) { await media.Command("pause"); return; }

            var pids = new HashSet<int>(owner.Windows.Select(w => (int)w.Pid));
            if (!owner.Token.Contains('!') && !owner.Token.Contains('\\'))
                foreach (var p in Process.GetProcessesByName(owner.Token)) pids.Add(p.Id);

            if (owner.Windows.Count == 0 && pids.Count == 0) { await media.Command("pause"); return; }

            foreach (var w in owner.Windows) WindowTracker.Close(w.Handle);
            await media.Command("pause");
            await Task.Delay(2500);

            // music players often keep running in the tray after their window closes — finish them off
            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.HasExited || Protected.Contains(p.ProcessName.ToLowerInvariant())) continue;
                    if (Browsers.Contains(p.ProcessName.ToLowerInvariant())) continue;
                    Log.Write($"closing media app {p.ProcessName} ({pid})");
                    p.Kill(true);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    void PushVolume(float level, bool muted, bool external) => Post(new { t = "vol", v = level, mute = muted, ext = external });

    void PushRadios() => Post(new { t = "radios", wifi = radios.WifiOn, bt = radios.BtOn });

    void PushSys()
    {
        var ps = SystemInformation.PowerStatus;
        bool hasBattery = !ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) && ps.BatteryLifePercent <= 1f;
        var msg = new
        {
            t = "sys",
            temp = sys.Temp is double d ? (int?)Math.Round(d) : null,
            load = (int)Math.Round(sys.Load),
            gpu = sys.Gpu is double g ? (int?)Math.Round(g) : null,
            batt = hasBattery ? (int?)Math.Round(ps.BatteryLifePercent * 100) : null,
            charging = ps.PowerLineStatus == PowerLineStatus.Online
        };
        var key = $"{msg.temp}/{msg.load}/{msg.gpu}/{msg.batt}/{msg.charging}";
        if (key == lastSysKey) return;
        lastSysKey = key;
        Post(msg);
    }

    void PushLevel()
    {
        if (!ready || hiddenForFullscreen || !media.Playing) return;
        Post(new { t = "level", v = Math.Round(audio.Peak, 3) });
    }

    // ───────────── housekeeping ─────────────

    void SlowTick()
    {
        tick++;
        PushWindows();
        if (ready) PollTelegram();
        foreach (var w in watching.ToList())
            if (w != "weather" && (w != "wifi" || tick % 4 == 0) && (w != "bt" || tick % 3 == 0)) PushWatched(w, false);
        if (tick % 2 == 0) PushSys();
        if (tick % 5 == 0) PushNet();
        if (ready) CheckCapture(true);
        if (tick % 5 == 0 && audio.CheckDevice()) PushVolume(audio.Level, audio.Muted, false);
        if (tick % 60 == 0) TopMost = true;

        // Over borderless/windowed-fullscreen games and videos the island stays on top (re-asserted every
        // second, games like to grab the top). Exclusive full screen (D3D) cannot be drawn over: there it hides.
        bool fs = FullscreenAppInFront(out bool exclusive);
        bool hide = settings.OverFullscreen ? exclusive : fs;
        if (fs && !hide) Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        if (hide != hiddenForFullscreen)
        {
            hiddenForFullscreen = hide;
            if (hide) Hide(); else Show();
        }
    }

    bool FullscreenAppInFront(out bool exclusive)
    {
        exclusive = false;
        if (Native.SHQueryUserNotificationState(out int state) == 0)
        {
            if (state == 3) { exclusive = true; return true; }   // exclusive Direct3D full screen
            if (state == 4) return true;                          // presentation mode
        }
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        if (Native.ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var mon = Screen.FromHandle(fg);
        if (!mon.Primary) return false;
        var b = mon.Bounds;
        if (r.Left > b.Left || r.Top > b.Top || r.Right < b.Right || r.Bottom < b.Bottom) return false;
        long style = (long)Native.GetWindowLongPtr(fg, Native.GWL_STYLE);
        return !(Native.IsZoomed(fg) && (style & Native.WS_CAPTION) == Native.WS_CAPTION);
    }

    void CursorTick()
    {
        if (!ready || !Visible) return;
        CheckCapture(false);
        Native.GetCursorPos(out var p);
        var local = new Point(p.X - Left, p.Y - Top);
        bool inside = regionCss.Any(r =>
            Rectangle.FromLTRB((int)(r.X * scale), (int)(r.Y * scale), (int)(r.Right * scale), (int)(r.Bottom * scale)).Contains(local));
        if (cursorInside && !inside) Post(new { t = "leave" });
        cursorInside = inside;
    }

    void CreateTray()
    {
        var auto = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = Autostart.Enabled, CheckOnClick = true };
        auto.CheckedChanged += (_, _) => { try { Autostart.Enabled = auto.Checked; } catch (Exception ex) { Log.Write(ex); } };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Островок") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(auto);
        var over = new ToolStripMenuItem("Поверх полноэкранных игр и видео") { Checked = settings.OverFullscreen, CheckOnClick = true };
        over.CheckedChanged += (_, _) => { settings.OverFullscreen = over.Checked; settings.Save(); };
        menu.Items.Add(over);
        menu.Items.Add("Обновить избранное", null, (_, _) => { if (ready) PushAll(); });
        menu.Items.Add("Папка настроек", null, (_, _) => { Directory.CreateDirectory(Log.Dir); Shell.Open(Log.Dir); });
        menu.Items.Add("Перезапустить", null, (_, _) => { Process.Start(Application.ExecutablePath); ExitApp(); });
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        tray = new NotifyIcon { Icon = MakeTrayIcon(), Text = "Островок", Visible = true, ContextMenuStrip = menu };
    }

    static Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var r = new RectangleF(2, 10, 28, 12);
            path.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
            path.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(18, 18, 22));
            using var pen = new Pen(Color.FromArgb(235, 235, 245), 2f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            using var dot = new SolidBrush(Color.FromArgb(91, 227, 143));
            g.FillEllipse(dot, 21, 13.5f, 5, 5);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    void ExitApp()
    {
        if (tray != null) tray.Visible = false;
        Application.Exit();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (fgHook != IntPtr.Zero) Native.UnhookWinEvent(fgHook);
        if (tray != null) tray.Visible = false;
        base.OnFormClosed(e);
    }
}
