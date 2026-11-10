using System.Data.OleDb;
using System.Runtime.InteropServices;

namespace Ostrovok;

/// <summary>
/// Search for the search cell: every installed app (the same list as "All apps" in Start),
/// pages of Windows Settings, and files from the Windows Search index.
/// </summary>
sealed class SearchHub
{
    #region shell enumeration of shell:AppsFolder
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    }
    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);
    static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    #endregion

    public sealed record App(string id, string name);
    sealed record SettingPage(string name, string uri, string glyph, string keys);

    List<App> apps = new();
    DateTime builtAt = DateTime.MinValue;

    static readonly SettingPage[] Settings =
    {
        new("Дисплей", "ms-settings:display", "monitor", "экран яркость разрешение масштаб монитор"),
        new("Звук", "ms-settings:sound", "vol", "громкость динамики наушники микрофон"),
        new("Bluetooth и устройства", "ms-settings:bluetooth", "bt", "блютуз наушники мышь клавиатура"),
        new("Сеть и Интернет", "ms-settings:network", "wifi", "интернет сеть ethernet"),
        new("Wi-Fi", "ms-settings:network-wifi", "wifi", "вайфай беспроводная сеть"),
        new("VPN", "ms-settings:network-vpn", "lock", "впн"),
        new("Мобильный хот-спот", "ms-settings:network-mobilehotspot", "wifi", "точка доступа раздача"),
        new("Персонализация", "ms-settings:personalization", "brush", "оформление тема"),
        new("Фон рабочего стола", "ms-settings:personalization-background", "photo", "обои картинка"),
        new("Цвета и тема", "ms-settings:colors", "brush", "тёмная светлая тема акцент"),
        new("Экран блокировки", "ms-settings:lockscreen", "monitor", "блокировка"),
        new("Панель задач", "ms-settings:taskbar", "grid", "таскбар"),
        new("Ночной свет", "ms-settings:nightlight", "sun", "синий свет вечер"),
        new("Центр обновления Windows", "ms-settings:windowsupdate", "refresh", "обновления апдейт"),
        new("Электропитание", "ms-settings:powersleep", "power", "сон питание батарея экран выключение"),
        new("Батарея", "ms-settings:batterysaver", "power", "заряд экономия энергии"),
        new("Уведомления", "ms-settings:notifications", "bell", "не беспокоить оповещения"),
        new("Мышь", "ms-settings:mousetouchpad", "mouse", "курсор сенсорная панель тачпад"),
        new("Клавиатура", "ms-settings:keyboard", "kbd", "раскладка ввод"),
        new("Язык и регион", "ms-settings:regionlanguage", "globe", "язык раскладка регион"),
        new("Дата и время", "ms-settings:dateandtime", "clock", "часы часовой пояс"),
        new("Установленные приложения", "ms-settings:appsfeatures", "grid", "удалить программы приложения"),
        new("Приложения по умолчанию", "ms-settings:defaultapps", "grid", "браузер по умолчанию"),
        new("Автозагрузка", "ms-settings:startupapps", "grid", "автозапуск запуск"),
        new("Память", "ms-settings:storagesense", "doc", "диск место хранилище"),
        new("О системе", "ms-settings:about", "monitor", "характеристики имя компьютера"),
        new("Учётные записи", "ms-settings:yourinfo", "user", "аккаунт пользователь"),
        new("Конфиденциальность", "ms-settings:privacy", "lock", "приватность разрешения камера местоположение"),
        new("Принтеры и сканеры", "ms-settings:printers", "doc", "печать принтер"),
        new("Игровой режим", "ms-settings:gaming-gamemode", "pad", "игры"),
    };

    /// <summary>Must be called on an STA thread (the UI thread).</summary>
    public void EnsureApps()
    {
        if ((DateTime.Now - builtAt).TotalMinutes < 10 && apps.Count > 0) return;
        var list = new List<App>();
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var p) == 0 && p != IntPtr.Zero)
            {
                var folder = (IShellItem)Marshal.GetObjectForIUnknown(p); Marshal.Release(p);
                var bhid = BHID_EnumItems; var eiid = typeof(IEnumShellItems).GUID;
                if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref eiid, out var pe) == 0 && pe != IntPtr.Zero)
                {
                    var e = (IEnumShellItems)Marshal.GetObjectForIUnknown(pe); Marshal.Release(pe);
                    while (e.Next(1, out var item, out uint got) == 0 && got == 1)
                    {
                        string name = Name(item, 0), parsing = Name(item, 0x80018001);
                        Marshal.ReleaseComObject(item);
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing)) continue;
                        if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Удал", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new App("shell:AppsFolder\\" + parsing, name));
                    }
                }
            }
        }
        catch (Exception ex) { Log.Write("apps index: " + ex.Message); }
        if (list.Count > 0) { apps = list; builtAt = DateTime.Now; }
        Log.Write($"apps indexed: {apps.Count}");
    }

    static string Name(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUni(p); CoTaskMemFree(p); return s;
    }

    static int Score(string text, string q)
    {
        var t = text.ToLowerInvariant();
        if (t.StartsWith(q)) return 3;
        if (t.Split(' ', '-', '_', '.', '(').Any(w => w.StartsWith(q))) return 2;
        return t.Contains(q) ? 1 : 0;
    }

    public List<App> FindApps(string q, int max) =>
        apps.Select(a => (a, s: Score(a.name, q))).Where(x => x.s > 0)
            .OrderByDescending(x => x.s).ThenBy(x => x.a.name.Length).Take(max).Select(x => x.a).ToList();

    public object FindSettings(string q) =>
        Settings.Select(s => (s, sc: Math.Max(Score(s.name, q), Score(s.keys, q) > 0 ? 1 : 0))).Where(x => x.sc > 0)
            .OrderByDescending(x => x.sc).Take(4).Select(x => new { id = x.s.uri, name = x.s.name, glyph = x.s.glyph }).ToList();

    /// <summary>Files and folders from the Windows Search index whose name contains q. Runs on any thread.</summary>
    public static List<(string path, string name, string folder)> FindFiles(string q, int max)
    {
        var list = new List<(string, string, string)>();
        string safe = new string(q.Where(c => c != '\'' && c != '%' && c != '_' && c != '[' && c != ']').ToArray()).Trim();
        if (safe.Length < 2) return list;
        try
        {
            using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            conn.Open();
            using var cmd = new OleDbCommand(
                $"SELECT TOP {max} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemFolderNameDisplay FROM SystemIndex " +
                $"WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%{safe}%' ORDER BY System.DateModified DESC", conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r[0] as string, r[1] as string, r[2] as string));
        }
        catch (Exception ex) { Log.Write("file search: " + ex.Message); }
        return list.Where(x => !string.IsNullOrEmpty(x.Item1)).ToList();
    }

    /// <summary>Recently opened files (Windows' own Recent list).</summary>
    public static List<(string lnk, string name, DateTime when)> RecentFiles(int max)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            return new DirectoryInfo(dir).GetFiles("*.lnk")
                .Where(f => Path.HasExtension(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderByDescending(f => f.LastWriteTime).Take(max)
                .Select(f => (f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTime)).ToList();
        }
        catch { return new(); }
    }
}
