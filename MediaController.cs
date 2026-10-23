using System.IO;
using Windows.Media.Control;
using Windows.Storage.Streams;
using TaskbarPlayer.Native;

namespace TaskbarPlayer;

internal sealed record MediaSource(string AppId, string Name);

/// <summary>
/// Wraps the Windows global media sessions (SMTC): Spotify, browsers playing YouTube Music, etc.
/// All public members must be used on the UI thread; WinRT events are marshalled there.
/// </summary>
internal sealed class MediaController : IDisposable
{
    private readonly Control _owner;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _sessionId;
    private volatile bool _sessionsDirty = true;
    private bool _refreshing;
    private bool _refreshAgain;
    private bool _disposed;
    private string? _mediaKey;
    private bool _coverLoaded;
    private TimeSpan _startTime;
    private TimeSpan _position;
    private DateTimeOffset _positionUpdatedAt;

    public MediaController(Control owner) => _owner = owner;

    /// <summary>Raised on the UI thread after the state was refreshed.</summary>
    public event EventHandler? Changed;

    /// <summary>App to control; null picks the active session automatically.</summary>
    public string? PreferredAppId { get; private set; }

    public bool HasSession => _session is not null;
    public string? SourceAppId => _sessionId;
    public string? SourceName => _sessionId is null ? null : FriendlyName(_sessionId);
    public string? Title { get; private set; }
    public string? Artist { get; private set; }

    /// <summary>Album art. Ownership passes to the consumer, which disposes it once replaced.</summary>
    public Image? Cover { get; private set; }

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public bool CanPrevious { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanSeek { get; private set; }
    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            if (Duration <= TimeSpan.Zero) return TimeSpan.Zero;
            var p = _position;
            if (IsPlaying && _positionUpdatedAt.Year > 2000)
            {
                p += DateTimeOffset.Now - _positionUpdatedAt;
            }
            return p < TimeSpan.Zero ? TimeSpan.Zero : p > Duration ? Duration : p;
        }
    }

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        RequestRefresh();
    }

    public void SetPreferredApp(string? appId)
    {
        PreferredAppId = appId;
        RequestRefresh();
    }

    public IReadOnlyList<MediaSource> GetSources()
    {
        if (_manager is null) return [];
        try
        {
            return _manager.GetSessions()
                .Select(s => s.SourceAppUserModelId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => new MediaSource(id, FriendlyName(id)))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public Task PlayPauseAsync() => RunAsync(s => s.TryTogglePlayPauseAsync().AsTask(), NativeMethods.VK_MEDIA_PLAY_PAUSE);
    public Task NextAsync() => RunAsync(s => s.TrySkipNextAsync().AsTask(), NativeMethods.VK_MEDIA_NEXT_TRACK);
    public Task PreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync().AsTask(), NativeMethods.VK_MEDIA_PREV_TRACK);

    public async Task SeekAsync(TimeSpan position)
    {
        if (_session is null || !CanSeek) return;
        try
        {
            if (await _session.TryChangePlaybackPositionAsync((_startTime + position).Ticks))
            {
                _position = position;
                _positionUpdatedAt = DateTimeOffset.Now;
            }
        }
        catch (Exception)
        {
            // Session went away; the next refresh picks that up.
        }
    }

    public void RequestRefresh()
    {
        if (_disposed) return;
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _ = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await RefreshCoreAsync();
                if (_disposed) return;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RunAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action, byte fallbackKey)
    {
        var session = _session;
        if (session is null)
        {
            NativeMethods.SendMediaKey(fallbackKey);
            return;
        }
        try
        {
            await action(session);
        }
        catch (Exception)
        {
            NativeMethods.SendMediaKey(fallbackKey);
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (_manager is null) return;

        GlobalSystemMediaTransportControlsSession? picked;
        try
        {
            picked = PickSession(_manager);
        }
        catch (Exception)
        {
            picked = null;
        }

        string? pickedId = picked?.SourceAppUserModelId;
        if (_sessionsDirty || !string.Equals(pickedId, _sessionId, StringComparison.OrdinalIgnoreCase))
        {
            _sessionsDirty = false;
            Unhook();
            _session = picked;
            _sessionId = pickedId;
            Hook();
        }

        var session = _session;
        if (session is null)
        {
            Clear();
            return;
        }

        try
        {
            var info = session.GetPlaybackInfo();
            var status = info.PlaybackStatus;
            IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            IsPaused = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            var controls = info.Controls;
            CanPrevious = controls.IsPreviousEnabled;
            CanNext = controls.IsNextEnabled;

            var timeline = session.GetTimelineProperties();
            _startTime = timeline.StartTime;
            var duration = timeline.EndTime - timeline.StartTime;
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            _position = timeline.Position - timeline.StartTime;
            _positionUpdatedAt = timeline.LastUpdatedTime;
            CanSeek = controls.IsPlaybackPositionEnabled && Duration > TimeSpan.Zero;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;

            Title = props.Title;
            Artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist : props.Artist;

            string key = $"{_sessionId}\n{props.Title}\n{props.Artist}\n{props.AlbumTitle}";
            if (key != _mediaKey)
            {
                _mediaKey = key;
                _coverLoaded = false;
                Cover = null;
            }

            // Apps often publish the artwork a moment after the title, so retry until we have it.
            if (!_coverLoaded && props.Thumbnail is not null)
            {
                _coverLoaded = true;
                var cover = await LoadCoverAsync(props.Thumbnail);
                if (_mediaKey == key)
                {
                    Cover = cover;
                }
                else
                {
                    cover?.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // The session can disappear between calls (app closed); the SessionsChanged event follows.
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();

        if (PreferredAppId is not null)
        {
            var preferred = sessions.FirstOrDefault(s => SameApp(s, PreferredAppId));
            if (preferred is not null) return preferred;
        }

        var current = manager.GetCurrentSession();
        if (current is not null && IsSessionPlaying(current)) return current;

        var playing = sessions.FirstOrDefault(IsSessionPlaying);
        if (playing is not null) return playing;

        // Nothing is playing: stick with what we had (e.g. paused Spotify) rather than jumping around.
        if (_sessionId is not null)
        {
            var previous = sessions.FirstOrDefault(s => SameApp(s, _sessionId));
            if (previous is not null) return previous;
        }

        return current ?? sessions.FirstOrDefault();
    }

    private static bool SameApp(GlobalSystemMediaTransportControlsSession s, string appId) =>
        string.Equals(s.SourceAppUserModelId, appId, StringComparison.OrdinalIgnoreCase);

    private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<Image?> LoadCoverAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var ras = await thumbnail.OpenReadAsync();
            using var stream = ras.AsStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Clear()
    {
        Title = null;
        Artist = null;
        Cover = null;
        IsPlaying = false;
        IsPaused = false;
        CanPrevious = false;
        CanNext = false;
        CanSeek = false;
        Duration = TimeSpan.Zero;
        _mediaKey = null;
        _coverLoaded = false;
    }

    private void Hook()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    private void Unhook()
    {
        if (_session is null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch (Exception)
        {
            // Session already gone.
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Post();

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        _sessionsDirty = true;
        Post();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Post();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Post();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Post();

    // WinRT raises events on thread-pool threads.
    private void Post()
    {
        if (_disposed || !_owner.IsHandleCreated || _owner.IsDisposed) return;
        try
        {
            _owner.BeginInvoke(new Action(RequestRefresh));
        }
        catch (InvalidOperationException)
        {
            // Window is closing.
        }
    }

    // Chromium extension id of the YouTube Music web app (same in Chrome, Edge and Brave).
    private const string YouTubeMusicPwaId = "cinhimbnkkghhklpknlkffjgod";

    private static readonly string[] BrowserNames =
        ["Google Chrome", "Microsoft Edge", "Firefox", "Brave", "Opera", "Vivaldi"];

    /// <summary>True for the YouTube Music app, or a browser (which may be playing YouTube Music in a tab).</summary>
    public bool IsYouTubeCapableSource =>
        SourceName is string name && (name == "YouTube Music" || BrowserNames.Contains(name));

    public static string FriendlyName(string appId)
    {
        string id = appId.ToLowerInvariant();
        // Installed web apps (PWAs) report the browser plus an app id; match them against the Start menu entries.
        var installed = MusicApps.Installed.FirstOrDefault(a => string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase));
        if (installed is not null) return installed.Name;
        if (id.Contains(YouTubeMusicPwaId)) return "YouTube Music";
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("music.youtube") || id.Contains("youtube music") || id.Contains("youtubemusic")) return "YouTube Music";
        if (id.Contains("msedge")) return "Microsoft Edge";
        if (id.Contains("chrome")) return "Google Chrome";
        if (id.Contains("firefox") || id.Contains("308046b0af4a39cb")) return "Firefox";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("vivaldi")) return "Vivaldi";
        if (id.Contains("applemusic") || id.Contains("appleinc")) return "Apple Music";
        if (id.Contains("zunemusic") || id.Contains("mediaplayer")) return "Media Player";
        if (id.Contains("deezer")) return "Deezer";
        if (id.Contains("tidal")) return "TIDAL";
        if (id.Contains("vlc")) return "VLC";

        // Packaged apps look like "Publisher.App_hash!App"; desktop apps like "foo.exe".
        string name = appId;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && bang > 0) name = name[(dot + 1)..];
        return name;
    }

    public void Dispose()
    {
        _disposed = true;
        Unhook();
        _session = null;
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }
    }
}
