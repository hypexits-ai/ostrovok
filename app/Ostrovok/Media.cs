using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Ostrovok;

/// <summary>Whatever is playing in Windows (Spotify, Yandex Music, browsers…), via the system media controls.</summary>
sealed class MediaWatcher
{
    GlobalSystemMediaTransportControlsSessionManager manager;
    GlobalSystemMediaTransportControlsSession session;
    readonly SemaphoreSlim gate = new(1, 1);
    string artKey;

    public bool Has, Playing, CanNext, CanPrev, CanSeek;
    public string Title, Artist, Art;

    /// <summary>Track position and length in seconds, as of now (0 when the app does not report them).</summary>
    public (double pos, double dur) Timeline()
    {
        try
        {
            var tl = session?.GetTimelineProperties();
            if (tl == null) return (0, 0);
            double dur = (tl.EndTime - tl.StartTime).TotalSeconds, pos = tl.Position.TotalSeconds;
            if (Playing && tl.LastUpdatedTime.Year > 2000) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
            return (Math.Clamp(pos, 0, Math.Max(dur, 0)), Math.Max(dur, 0));
        }
        catch { return (0, 0); }
    }

    /// <summary>Which app owns the current session: an exe name ("Spotify.exe") or an AppUserModelID.</summary>
    public string SourceApp
    {
        get { try { return session?.SourceAppUserModelId; } catch { return null; } }
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action Changed;

    public async Task InitAsync()
    {
        manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        manager.CurrentSessionChanged += (_, _) => Attach();
        manager.SessionsChanged += (_, _) => Attach();
        Attach();
    }

    GlobalSystemMediaTransportControlsSession Pick()
    {
        var current = manager.GetCurrentSession();
        if (current != null) return current;
        var all = manager.GetSessions();
        return all.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing) ?? all.FirstOrDefault();
    }

    readonly object attachLock = new();

    void Attach()
    {
        lock (attachLock)
        {
            try
            {
                var next = Pick();
                string nextId = SafeId(next), curId = SafeId(session);
                if (next == null || session == null || nextId != curId)
                {
                    var old = session;
                    session = next;
                    // the old session may already be gone; unsubscribing from it can throw
                    if (old != null)
                    {
                        try { old.MediaPropertiesChanged -= OnProps; } catch { }
                        try { old.PlaybackInfoChanged -= OnPlayback; } catch { }
                        try { old.TimelinePropertiesChanged -= OnTimeline; } catch { }
                    }
                    if (next != null)
                    {
                        next.MediaPropertiesChanged += OnProps;
                        next.PlaybackInfoChanged += OnPlayback;
                        next.TimelinePropertiesChanged += OnTimeline;
                    }
                }
            }
            catch (Exception ex) { Log.Write("media attach: " + ex.Message); }
        }
        _ = RefreshAsync();
    }

    static string SafeId(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s?.SourceAppUserModelId; } catch { return null; }
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();
    // timeline updates arrive often; only tell the page, no need to reload the cover
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => TimelineChanged?.Invoke();

    /// <summary>Raised on a background thread when the track position jumps.</summary>
    public event Action TimelineChanged;

    public async Task RefreshAsync()
    {
        await gate.WaitAsync();
        try
        {
            var s = session;
            if (s == null)
            {
                Has = Playing = false; Title = Artist = Art = null; artKey = null;
            }
            else
            {
                var pb = s.GetPlaybackInfo();
                Playing = pb?.PlaybackStatus == PlaybackStatus.Playing;
                CanNext = pb?.Controls?.IsNextEnabled ?? false;
                CanPrev = pb?.Controls?.IsPreviousEnabled ?? false;
                CanSeek = pb?.Controls?.IsPlaybackPositionEnabled ?? false;

                var props = await s.TryGetMediaPropertiesAsync();
                Title = props?.Title ?? "";
                Artist = props?.Artist;
                if (string.IsNullOrEmpty(Artist)) Artist = props?.AlbumArtist ?? "";
                Has = !string.IsNullOrWhiteSpace(Title);

                var key = Title + "\u0001" + Artist;
                if (key != artKey) { Art = null; artKey = key; }
                if (Art == null && props?.Thumbnail != null) Art = await LoadArt(props.Thumbnail);
            }
        }
        catch (Exception ex) { Log.Write("media: " + ex.Message); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    static async Task<string> LoadArt(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            using var src = new Bitmap(ms);
            int side = Math.Min(src.Width, src.Height);
            var crop = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
            int outSide = Math.Min(160, side);
            using var dst = new Bitmap(outSide, outSide);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, outSide, outSide), crop, GraphicsUnit.Pixel);
            }
            using var outMs = new MemoryStream();
            dst.Save(outMs, ImageFormat.Jpeg);
            return "data:image/jpeg;base64," + Convert.ToBase64String(outMs.ToArray());
        }
        catch { return null; }
    }

    public async Task Command(string c, double value = 0)
    {
        var s = session;
        if (s == null) return;
        try
        {
            switch (c)
            {
                case "seek": await s.TryChangePlaybackPositionAsync((long)(value * 10_000_000)); break;
                case "play": await s.TryTogglePlayPauseAsync(); break;
                case "next": await s.TrySkipNextAsync(); break;
                case "prev": await s.TrySkipPreviousAsync(); break;
                case "pause": await s.TryPauseAsync(); break;
            }
        }
        catch (Exception ex) { Log.Write("media cmd: " + ex.Message); }
    }
}
