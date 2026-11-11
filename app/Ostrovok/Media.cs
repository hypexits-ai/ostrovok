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
                // compare the objects, not the app names: Yandex Music builds a new session with the same name
                // on every track switch, and staying on the old one left the island without updates for seconds
                if (!ReferenceEquals(next, session))
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

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();
    // timeline updates arrive often; only tell the page, no need to reload the cover
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => TimelineChanged?.Invoke();

    /// <summary>Raised on a background thread when the track position jumps.</summary>
    public event Action TimelineChanged;

    // While an app switches tracks it answers with nothing for a while: Yandex Music even removes its session
    // until the next track has loaded, which took up to 7 s in tests. The island keeps the last track on screen
    // through such gaps (Switching) instead of "nothing is playing": up to LongGrace after a button press or
    // while music was playing (a track ending switches too), else Grace (the app was probably closed).
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(3), LongGrace = TimeSpan.FromSeconds(15);
    // After a new title the app may still hand out the old cover, or none yet: keep looking for the new one this long.
    static readonly TimeSpan ArtWindow = TimeSpan.FromSeconds(4);
    // a button pressed while the app had no session is sent once it is back, if that happens soon enough
    static readonly TimeSpan PendingLife = TimeSpan.FromSeconds(12);
    DateTime lastGood = DateTime.MinValue, keyChangedAt = DateTime.MinValue, lastCommandAt = DateTime.MinValue, pendingAt;
    string artHash, staleHash, signature, pendingCmd;
    bool thumbMissing, lastPlaying;
    int retryQueued;

    /// <summary>The app is between tracks: the last track is still shown, the next one is not known yet.</summary>
    public bool Switching;
    // covers of recent tracks: going back to one shows its cover at once, while the app is still loading it
    readonly Dictionary<string, (string img, string hash)> artCache = new();
    readonly Queue<string> artOrder = new();

    void Remember(string key, string img, string hash)
    {
        if (!artCache.ContainsKey(key))
        {
            artOrder.Enqueue(key);
            if (artOrder.Count > 40) artCache.Remove(artOrder.Dequeue());
        }
        artCache[key] = (img, hash);
    }

    public async Task RefreshAsync()
    {
        bool retry = false, changed;
        string sendNow = null;
        await gate.WaitAsync();
        try
        {
            var s = session;
            var now = DateTime.Now;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo pb = null;
            GlobalSystemMediaTransportControlsSessionMediaProperties props = null;
            if (s != null)
            {
                try { pb = s.GetPlaybackInfo(); props = await s.TryGetMediaPropertiesAsync(); }
                catch { props = null; }   // a session that is being replaced can throw for a moment
            }

            if (string.IsNullOrWhiteSpace(props?.Title))
            {
                var grace = lastPlaying || now - lastCommandAt < LongGrace ? LongGrace : Grace;
                if (Has && now - lastGood < grace) { retry = true; Switching = true; }   // a gap: keep the last track
                else if (Has || Title != null)
                {
                    Has = Playing = Switching = false; Title = Artist = Art = null;
                    artKey = artHash = staleHash = pendingCmd = null;
                }
            }
            else
            {
                lastGood = now;
                // back after a gap: the app is often still fetching the cover, so give it the full wait from now
                if (Switching) keyChangedAt = now;
                Has = true; Switching = false;
                if (pendingCmd != null)
                {
                    if (now - pendingAt < PendingLife) sendNow = pendingCmd;
                    pendingCmd = null;
                }
                Playing = pb?.PlaybackStatus == PlaybackStatus.Playing;
                lastPlaying = Playing;
                CanNext = pb?.Controls?.IsNextEnabled ?? false;
                CanPrev = pb?.Controls?.IsPreviousEnabled ?? false;
                CanSeek = pb?.Controls?.IsPlaybackPositionEnabled ?? false;
                Title = props.Title;
                Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;

                var key = Title + "\u0001" + Artist;
                if (key != artKey)
                {
                    artKey = key; keyChangedAt = now; staleHash = artHash;
                    if (artCache.TryGetValue(key, out var known)) { Art = known.img; artHash = known.hash; }
                }
                // The cover is checked on every look (cheap: the bytes are hashed, and decoded only when they change).
                // Right after a new title the app may still hand out the previous track's cover, or none: the old
                // picture then stays until the new one arrives, so it never blinks to a placeholder or a wrong cover.
                bool inWindow = now - keyChangedAt < ArtWindow;
                var (bytes, hash) = props.Thumbnail != null ? await ReadThumb(props.Thumbnail) : (null, null);
                bool oldCover = hash != null && hash == staleHash;
                thumbMissing = hash == null;
                if (hash == null || oldCover) { if (inWindow) retry = true; }
                else if (hash != artHash)
                {
                    var img = MakeArt(bytes);
                    if (img != null) { Art = img; artHash = hash; }
                }
                if (hash != null && !oldCover && hash == artHash) Remember(key, Art, hash);
                if (!inWindow && thumbMissing && artHash != null && artHash == staleHash)
                {
                    Art = null; artHash = null;   // the new track really has no cover: do not leave the old one
                }
            }
            var sig = $"{Has}|{Switching}|{Playing}|{Title}|{Artist}|{artHash}|{CanNext}|{CanPrev}|{CanSeek}";
            changed = sig != signature; signature = sig;
        }
        catch (Exception ex) { Log.Write("media: " + ex.Message); changed = true; }
        finally { gate.Release(); }
        if (retry) RetrySoon();
        if (changed) Changed?.Invoke();
        if (sendNow != null) _ = Command(sendNow, 0, queued: true);
    }

    // the session may vanish and come back as a new object: look for it again, not only at the old one
    void RetrySoon(int ms = 350)
    {
        if (Interlocked.Exchange(ref retryQueued, 1) == 1) return;
        _ = Task.Delay(ms).ContinueWith(_ => { Interlocked.Exchange(ref retryQueued, 0); Recheck(); });
    }

    /// <summary>A safety net for missed events: look again at the session and what it plays.</summary>
    public void Recheck()
    {
        if (manager == null) return;
        Attach();
    }

    static async Task<(byte[] bytes, string hash)> ReadThumb(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            if (ms.Length == 0) return (null, null);
            var bytes = ms.ToArray();
            return (bytes, Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)));
        }
        catch { return (null, null); }
    }

    /// <summary>A square 160px JPEG of the cover, as a data URL for the page.</summary>
    static string MakeArt(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
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

    public async Task Command(string c, double value = 0, bool queued = false)
    {
        bool transport = c is "next" or "prev" or "play" or "pause";
        if (transport && !queued) lastCommandAt = DateTime.Now;
        var s = session;
        bool ok = false;
        if (s != null)
        {
            try
            {
                ok = c switch
                {
                    "seek" => await s.TryChangePlaybackPositionAsync((long)(value * 10_000_000)),
                    "play" => await s.TryTogglePlayPauseAsync(),
                    "next" => await s.TrySkipNextAsync(),
                    "prev" => await s.TrySkipPreviousAsync(),
                    "pause" => await s.TryPauseAsync(),
                    _ => false
                };
            }
            catch (Exception ex) { Log.Write("media cmd: " + ex.Message); }
        }
        if (!transport) return;
        if (!ok && !queued && Has)
        {
            // the app is between tracks and has no session to take the press: send it once the app is back
            pendingCmd = c; pendingAt = DateTime.Now;
            RetrySoon();
        }
        // the app may rebuild its session or update the track a moment later: look again a few times
        foreach (var ms in new[] { 300, 900, 2000 })
            _ = Task.Delay(ms).ContinueWith(_ => Recheck());
    }
}
