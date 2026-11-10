using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace Ostrovok;

/// <summary>
/// Telegram for the island, from two sources that need no extra rights:
///  • the unread counter — Telegram keeps it in its (hidden) main window title, "Telegram (3)";
///  • full messages — when Telegram uses Windows notifications, Windows keeps every toast in its local
///    notification history (wpndatabase.db); new Telegram rows are read from there, read-only.
/// Nothing leaves the computer.
/// </summary>
sealed class TelegramWatcher
{
    static readonly string Db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db");
    static readonly Regex TitleRx = new(@"^Telegram(?:\s*\((\d+)\))?$", RegexOptions.Compiled);

    long lastId = -1;
    int pid;
    public string ExePath { get; private set; }
    public int Unread { get; private set; } = -1;
    public bool NativeSeen { get; private set; }

    public sealed record Message(string name, string text, string avatar);

    /// <summary>Unread messages, or -1 when Telegram is not running.</summary>
    public int ReadUnread()
    {
        try
        {
            if (pid == 0 || !Alive(pid))
            {
                pid = 0;
                var p = Process.GetProcessesByName("Telegram").FirstOrDefault();
                if (p == null) { Unread = -1; return Unread; }
                pid = p.Id;
                ExePath = AudioHub.ProcessPath((uint)pid);
            }
            int found = -1;
            Native.EnumWindows((h, _) =>
            {
                Native.GetWindowThreadProcessId(h, out uint wpid);
                if (wpid != pid) return true;
                var m = TitleRx.Match(Native.Title(h));
                if (!m.Success) return true;
                found = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
                return false;
            }, IntPtr.Zero);
            Unread = found;
        }
        catch { }
        return Unread;
    }

    static bool Alive(int id)
    {
        try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch { return false; }
    }

    /// <summary>New Telegram toasts since the last call (the first call only remembers where history ends).</summary>
    public List<Message> ReadNew()
    {
        var result = new List<Message>();
        if (!File.Exists(Db)) return result;
        try
        {
            using var con = new SqliteConnection($"Data Source={Db};Mode=ReadOnly;Cache=Shared");
            con.Open();
            long max;
            using (var c = con.CreateCommand()) { c.CommandText = "SELECT IFNULL(MAX(Id),0) FROM Notification"; max = Convert.ToInt64(c.ExecuteScalar()); }
            if (lastId < 0) { lastId = max; return result; }
            if (max <= lastId) { lastId = Math.Min(lastId, max); return result; }

            using (var c = con.CreateCommand())
            {
                c.CommandText = "SELECT n.Id, n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                                "WHERE n.Id > $last AND n.Type = 'toast' AND h.PrimaryId LIKE '%Telegram%' ORDER BY n.Id";
                c.Parameters.AddWithValue("$last", lastId);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var bytes = (byte[])r[1];
                    var msg = Parse(Encoding.UTF8.GetString(bytes));
                    if (msg != null) { result.Add(msg); NativeSeen = true; }
                }
            }
            lastId = max;
        }
        catch (Exception ex) { Log.Write("telegram db: " + ex.Message); }
        return result;
    }

    static Message Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
            if (texts.Count == 0) return null;
            string name = texts[0], text = string.Join(" · ", texts.Skip(1));
            string avatar = null;
            var src = doc.Descendants("image").Select(i => (string)i.Attribute("src")).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (src != null && Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
            {
                var file = new FileInfo(uri.LocalPath);
                if (file.Length < 600_000)
                {
                    var mime = file.Extension.ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", _ => "image/png" };
                    avatar = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file.FullName))}";
                }
            }
            return new Message(name, text, avatar);
        }
        catch { return null; }
    }

    /// <summary>Brings Telegram forward (starting it again just activates the running copy).</summary>
    public void Open()
    {
        try { if (ExePath != null) Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("telegram open: " + ex.Message); }
    }
}
