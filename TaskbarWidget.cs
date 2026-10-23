using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TaskbarPlayer.Native;
using static TaskbarPlayer.Native.NativeMethods;

namespace TaskbarPlayer;

internal enum WidgetPart
{
    None,
    Info,
    Previous,
    PlayPause,
    Next,
    Volume,
    Progress,
    AddToPlaylist,
}

internal enum WidgetPlacement
{
    Auto,
    Left,
    Right,
}

/// <summary>
/// A layered child window hosted inside the Windows taskbar (Shell_TrayWnd), drawn with per-pixel alpha
/// so it blends with the taskbar's own background.
/// </summary>
internal sealed class TaskbarWidget : NativeWindow, IDisposable
{
    private readonly MediaController _media;
    private readonly AppSettings _settings;
    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 600 };

    private IntPtr _tray;
    private Rectangle _bounds;
    private float _scale = 1f;
    private bool _showInfo;
    private float _textWidth;
    private float _buttonWidth = 34;
    private TaskbarLayout? _layout;
    private bool _probing;
    private bool? _autoUsesLeft;
    private RectangleF _infoRect;
    private readonly Dictionary<WidgetPart, RectangleF> _buttonRects = new();
    private RectangleF _progressRect;
    private float _mouseX;
    private float? _seekFraction; // set while dragging on the timeline
    private WidgetPart _hover;
    private WidgetPart _pressed;
    private bool _trackingMouse;
    private float _volume;
    private bool _muted;

    public TaskbarWidget(MediaController media, AppSettings settings)
    {
        _media = media;
        _settings = settings;
        _hoverTimer.Tick += (_, _) => ShowHoverTip();
    }

    public event Action<WidgetPart, Rectangle>? PartClicked;
    public event Action<Point>? ContextMenuRequested;
    public event Action<int>? WheelScrolled;

    /// <summary>The user clicked or dragged on the timeline; the argument is the target position (0..1).</summary>
    public event Action<float>? SeekRequested;

    /// <summary>Text shown when no media session is available (e.g. API failure).</summary>
    public string? StatusText { get; set; }

    /// <summary>Decides whether the add-to-playlist button is shown (YouTube Music sources only).</summary>
    public Func<bool>? PlaylistButtonVisible { get; set; }

    private string? _flashText;
    private DateTime _flashUntil;

    /// <summary>Temporarily replaces the second line with a message, e.g. "Added to My playlist".</summary>
    public void Flash(string text, TimeSpan duration)
    {
        _flashText = text;
        _flashUntil = DateTime.UtcNow + duration;
        Render();
    }

    private bool ShowsPlaylistButton => PlaylistButtonVisible?.Invoke() == true;

    private WidgetPart[] ButtonParts => ShowsPlaylistButton
        ? [WidgetPart.Previous, WidgetPart.PlayPause, WidgetPart.Next, WidgetPart.AddToPlaylist, WidgetPart.Volume]
        : [WidgetPart.Previous, WidgetPart.PlayPause, WidgetPart.Next, WidgetPart.Volume];

    public bool IsAttached => Handle != IntPtr.Zero && IsWindow(Handle) && _tray != IntPtr.Zero && IsWindow(_tray);

    public Rectangle TrayScreenBounds => GetWindowRect(_tray, out var r) ? r.ToRectangle() : Rectangle.Empty;

    /// <summary>Creates (or re-creates after an Explorer restart) the child window inside the taskbar.</summary>
    public bool Attach()
    {
        IntPtr tray = FindTray();
        if (tray == IntPtr.Zero) return false;

        if (Handle != IntPtr.Zero)
        {
            if (IsWindow(Handle) && tray == _tray) return true;
            if (IsWindow(Handle)) DestroyHandle(); else ReleaseHandle();
        }

        _tray = tray;
        _bounds = Rectangle.Empty;
        _layout = null;
        _autoUsesLeft = null;
        try
        {
            CreateHandle(new CreateParams
            {
                Caption = "TaskbarPlayer",
                Parent = tray,
                Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                Width = 1,
                Height = 1,
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Attach to taskbar 0x{tray:X} failed: {ex}");
            _tray = IntPtr.Zero;
            return false;
        }
        Log.Write($"Attached to taskbar 0x{tray:X}, widget 0x{Handle:X}");
        UpdateLayout();
        return true;
    }

    /// <summary>All taskbars: the main one plus one per extra monitor (when shown on all displays).</summary>
    public static IReadOnlyList<(IntPtr Tray, Screen Screen)> FindTaskbars()
    {
        var bars = new List<(IntPtr, Screen)>();
        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) bars.Add((primary, Screen.FromHandle(primary)));

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (IsWindowVisible(secondary)) bars.Add((secondary, Screen.FromHandle(secondary)));
        }
        return bars;
    }

    /// <summary>The taskbar on the chosen monitor, falling back to the main taskbar when that monitor is gone.</summary>
    private IntPtr FindTray()
    {
        var bars = FindTaskbars();
        if (bars.Count == 0) return IntPtr.Zero;
        if (_settings.Monitor is string device)
        {
            foreach (var (tray, screen) in bars)
            {
                if (string.Equals(screen.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return tray;
            }
        }
        return bars[0].Tray;
    }

    public void SetVolume(float level, bool muted)
    {
        if (Math.Abs(level - _volume) < 0.001f && muted == _muted) return;
        _volume = level;
        _muted = muted;
        Render();
    }

    /// <summary>Called periodically: keeps the widget attached, positioned and its progress line current.</summary>
    public void Tick()
    {
        // Also re-attaches when the chosen monitor (re)appears or the setting changed.
        if (!IsAttached || FindTray() != _tray)
        {
            Attach();
            return;
        }
        ProbeTaskbar();
        bool moved = UpdateLayout();
        // Explorer occasionally re-orders its own children; stay on top of the XAML content.
        SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        bool flashExpired = _flashText is not null && DateTime.UtcNow >= _flashUntil;
        if (flashExpired) _flashText = null;
        if (!moved && (flashExpired || (_media.IsPlaying && _media.Duration > TimeSpan.Zero))) Render();
    }

    /// <summary>Measures the free taskbar space on a background thread, then re-lays out.</summary>
    private async void ProbeTaskbar()
    {
        if (_probing) return;
        _probing = true;
        try
        {
            IntPtr tray = _tray;
            var layout = await Task.Run(() => TaskbarProbe.Probe(tray));
            if (tray != _tray) return;
            _layout = layout;
            UpdateLayout();
        }
        finally
        {
            _probing = false;
        }
    }

    /// <summary>Recomputes size and position from the current taskbar layout. Returns true if it changed.</summary>
    public bool UpdateLayout()
    {
        if (!IsAttached || !GetWindowRect(_tray, out var trayRect)) return false;
        var tray = trayRect.ToRectangle();

        uint dpi = GetDpiForWindow(_tray);
        _scale = dpi == 0 ? 1f : dpi / 96f;
        float s = _scale;
        int margin = (int)(8 * s);

        // Everything below is in screen coordinates until the final conversion.
        Rectangle band;
        Rectangle gap;
        bool alignLeft;
        if (_layout is TaskbarLayout layout)
        {
            band = layout.Band;
            bool useLeft = ChooseLeftGap(layout, s);
            gap = useLeft ? layout.LeftGap : layout.RightGap;
            alignLeft = useLeft; // left gap: next to Widgets; right gap: next to the system tray
        }
        else
        {
            // Fallback (Windows 10, or UI Automation unavailable): classic child windows.
            Rectangle notify = ChildScreenRect("TrayNotifyWnd") ?? new Rectangle(tray.Right, tray.Top, 0, tray.Height);
            Rectangle tasks = ChildScreenRect("ReBarWindow32") ?? new Rectangle(tray.Left, tray.Top, 0, tray.Height);
            band = Rectangle.FromLTRB(tray.Left, tasks.Top, tray.Right, tasks.Bottom);
            gap = Rectangle.FromLTRB(tasks.Right, band.Top, Math.Max(tasks.Right, notify.Left), band.Bottom);
            alignLeft = false;
        }

        int height = band.Height;
        int available = gap.Width - 2 * margin;

        int buttonCount = ButtonParts.Length;
        _buttonWidth = Math.Clamp(available / (float)buttonCount, 26 * s, 34 * s);
        float buttonsWidth = buttonCount * _buttonWidth;
        float infoFixed = 4 * s + (_settings.ShowCover ? CoverSize(height) + 8 * s : 0);
        float textWidth = 0;
        bool showInfo;
        if (_settings.ShowTitle)
        {
            textWidth = Math.Min(180 * s, available - buttonsWidth - infoFixed - 6 * s);
            showInfo = textWidth >= 70 * s;
        }
        else
        {
            showInfo = _settings.ShowCover && available >= buttonsWidth + infoFixed + 6 * s;
        }
        if (!showInfo) textWidth = 0;

        int width = (int)Math.Ceiling(showInfo ? infoFixed + textWidth + 6 * s + buttonsWidth : buttonsWidth);
        int x = alignLeft ? gap.Left + margin : gap.Right - margin - width;

        // Child window coordinates are relative to the taskbar window.
        var bounds = new Rectangle(x - tray.Left, band.Top - tray.Top, width, height);

        bool changed = bounds != _bounds || showInfo != _showInfo || Math.Abs(textWidth - _textWidth) > 0.5f
            || buttonCount != _buttonRects.Count;
        if (!changed) return false;

        _bounds = bounds;
        _showInfo = showInfo;
        _textWidth = textWidth;
        ComputeParts();
        SetWindowPos(Handle, HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);
        Render();
        return true;
    }

    private bool ChooseLeftGap(TaskbarLayout layout, float s)
    {
        switch (_settings.Placement)
        {
            case WidgetPlacement.Left: return true;
            case WidgetPlacement.Right: return false;
        }

        int left = layout.LeftGap.Width;
        int right = layout.RightGap.Width;
        if (_autoUsesLeft is not bool current)
        {
            current = left > right;
        }
        else
        {
            // Hysteresis: only switch sides when the other side is clearly roomier, to avoid jumping around.
            int mine = current ? left : right;
            int other = current ? right : left;
            float minimum = 4 * 26 * s + 16 * s;
            if (other > mine + 40 * s || (mine < minimum && other >= minimum)) current = !current;
        }
        _autoUsesLeft = current;
        return current;
    }

    private float CoverSize(int height) => Math.Min(height - 14 * _scale, 32 * _scale);

    private Rectangle? ChildScreenRect(string className)
    {
        IntPtr child = FindWindowEx(_tray, IntPtr.Zero, className, null);
        if (child == IntPtr.Zero || !IsWindowVisible(child) || !GetWindowRect(child, out var r)) return null;
        return r.ToRectangle();
    }

    private void ComputeParts()
    {
        float s = _scale;
        float h = _bounds.Height;
        float x = 0;
        _infoRect = RectangleF.Empty;
        if (_showInfo)
        {
            float infoWidth = 4 * s + (_settings.ShowCover ? CoverSize(_bounds.Height) + 8 * s : 0) + _textWidth;
            _infoRect = new RectangleF(0, 0, infoWidth, h);
            x = infoWidth + 6 * s;
        }

        _buttonRects.Clear();
        foreach (var part in ButtonParts)
        {
            _buttonRects[part] = new RectangleF(x, 0, _buttonWidth, h);
            x += _buttonWidth;
        }
    }

    // ---------------------------------------------------------------- drawing

    public void Render()
    {
        if (!IsAttached || _bounds.Width <= 0 || _bounds.Height <= 0) return;

        float s = _scale;
        bool light = Glyphs.IsLightTaskbar();
        Color fg = light ? Color.FromArgb(235, 0, 0, 0) : Color.White;
        Color fgDim = Color.FromArgb(light ? 150 : 170, fg);
        Color fgFaint = Color.FromArgb(light ? 60 : 70, fg);

        using var bmp = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            // Alpha 1 keeps the whole widget clickable while being visually transparent.
            g.Clear(Color.FromArgb(1, 0, 0, 0));

            DrawHover(g, light);
            if (_showInfo) DrawInfo(g, fg, fgDim, fgFaint);

            bool hasSession = _media.HasSession;
            DrawGlyph(g, WidgetPart.Previous, Glyphs.Previous, 15, !hasSession || _media.CanPrevious, fg);
            DrawGlyph(g, WidgetPart.PlayPause, _media.IsPlaying ? Glyphs.Pause : Glyphs.Play, 18, true, fg);
            DrawGlyph(g, WidgetPart.Next, Glyphs.Next, 15, !hasSession || _media.CanNext, fg);
            DrawGlyph(g, WidgetPart.AddToPlaylist, Glyphs.AddToPlaylist, 15, true, fg);
            DrawGlyph(g, WidgetPart.Volume, Glyphs.ForVolume(_volume, _muted), 16, true, fg);
        }
        UpdateLayered(Handle, bmp);
    }

    private void DrawHover(Graphics g, bool light)
    {
        if (_hover is WidgetPart.None or WidgetPart.Progress) return;
        RectangleF rect = _hover == WidgetPart.Info ? _infoRect : _buttonRects.GetValueOrDefault(_hover);
        if (rect.IsEmpty) return;

        float s = _scale;
        float inset = Math.Max(4 * s, (rect.Height - 40 * s) / 2);
        rect = RectangleF.FromLTRB(rect.Left + 1 * s, rect.Top + inset, rect.Right - 1 * s, rect.Bottom - inset);
        int alpha = _pressed == _hover ? 14 : 24;
        using var brush = new SolidBrush(light ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255));
        using var path = Glyphs.RoundedRect(rect, 4 * s);
        g.FillPath(brush, path);
    }

    private void DrawInfo(Graphics g, Color fg, Color fgDim, Color fgFaint)
    {
        float s = _scale;
        float h = _bounds.Height;
        float x = 4 * s;

        if (_settings.ShowCover)
        {
            float size = CoverSize(_bounds.Height);
            var rect = new RectangleF(x, (h - size) / 2, size, size);
            using var path = Glyphs.RoundedRect(rect, 4 * s);
            var cover = _media.Cover;
            if (cover is not null)
            {
                var state = g.Save();
                g.SetClip(path);
                int side = Math.Min(cover.Width, cover.Height);
                var src = new Rectangle((cover.Width - side) / 2, (cover.Height - side) / 2, side, side);
                g.DrawImage(cover, rect, src, GraphicsUnit.Pixel);
                g.Restore(state);
            }
            else
            {
                using var bg = new SolidBrush(fgFaint);
                g.FillPath(bg, path);
                DrawCenteredGlyph(g, Glyphs.MusicNote, rect, size * 0.5f, fgDim);
            }
            x += size + 8 * s;
        }

        if (_textWidth <= 0) return;

        string title;
        string? subtitle;
        if (_media.HasSession)
        {
            title = string.IsNullOrWhiteSpace(_media.Title) ? _media.SourceName ?? Strings.Unknown : _media.Title!;
            subtitle = string.IsNullOrWhiteSpace(_media.Artist) ? _media.SourceName : _media.Artist;
            if (_flashText is not null && DateTime.UtcNow < _flashUntil) subtitle = _flashText;
        }
        else
        {
            title = StatusText ?? Strings.NothingPlaying;
            subtitle = StatusText is null ? Strings.ClickToStartApp : null;
        }

        using var titleFont = new Font("Segoe UI Semibold", 12.5f * s, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 11.5f * s, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        bool hasProgress = _media.HasSession && _media.Duration > TimeSpan.Zero;
        bool seeking = hasProgress && _media.CanSeek && (_seekFraction is not null || _hover == WidgetPart.Progress);
        if (seeking)
        {
            // While pointing at the timeline, the second line shows the time under the cursor.
            float at = _seekFraction ?? FractionAt(_mouseX);
            subtitle = $"{FormatTime(_media.Duration * at)} / {FormatTime(_media.Duration)}";
        }

        float titleH = titleFont.GetHeight(g);
        float subH = subtitle is null ? 0 : subFont.GetHeight(g);
        float progressH = hasProgress ? 6 * s : 0;
        float y = (h - titleH - subH - progressH) / 2;

        using (var brush = new SolidBrush(fg))
        {
            g.DrawString(title, titleFont, brush, new RectangleF(x, y, _textWidth, titleH), format);
        }
        y += titleH;
        if (subtitle is not null)
        {
            using var brush = new SolidBrush(fgDim);
            g.DrawString(subtitle, subFont, brush, new RectangleF(x, y, _textWidth, subH), format);
            y += subH;
        }

        _progressRect = RectangleF.Empty;
        if (hasProgress)
        {
            float barY = y + 3 * s;
            float barX = x + 1 * s;
            float barW = _textWidth - 4 * s;
            _progressRect = new RectangleF(barX, barY, barW, 2 * s);

            float fraction = _seekFraction ?? (float)(_media.Position.TotalSeconds / _media.Duration.TotalSeconds);
            fraction = Math.Clamp(fraction, 0f, 1f);
            float thickness = seeking ? 4 * s : 2 * s;
            float top = barY + 1 * s - thickness / 2;
            using var track = new SolidBrush(fgFaint);
            using var fill = new SolidBrush(seeking ? fg : fgDim);
            g.FillRectangle(track, barX, top, barW, thickness);
            g.FillRectangle(fill, barX, top, barW * fraction, thickness);
            if (seeking)
            {
                float r = 5 * s;
                g.FillEllipse(fill, barX + barW * fraction - r, barY + 1 * s - r, 2 * r, 2 * r);
            }
        }
    }

    private float FractionAt(float x) =>
        _progressRect.Width <= 0 ? 0f : Math.Clamp((x - _progressRect.Left) / _progressRect.Width, 0f, 1f);

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void DrawGlyph(Graphics g, WidgetPart part, string glyph, float sizeDip, bool enabled, Color fg)
    {
        if (!_buttonRects.TryGetValue(part, out var rect)) return;
        var color = enabled ? fg : Color.FromArgb(80, fg);
        DrawCenteredGlyph(g, glyph, rect, sizeDip * _scale, color);
    }

    private static void DrawCenteredGlyph(Graphics g, string glyph, RectangleF rect, float pixelSize, Color color)
    {
        using var font = new Font(Glyphs.FontFamilyName, pixelSize, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(glyph, font, brush, rect, format);
    }

    // ---------------------------------------------------------------- input

    private WidgetPart HitTest(Point p)
    {
        if (!_progressRect.IsEmpty && _media.CanSeek)
        {
            // The bar is only 2px high; give it a comfortable hit area.
            float s = _scale;
            var hit = RectangleF.FromLTRB(_progressRect.Left - 3 * s, _progressRect.Top - 6 * s,
                _progressRect.Right + 3 * s, Math.Max(_progressRect.Bottom + 8 * s, _bounds.Height - 4 * s));
            if (hit.Contains(p)) return WidgetPart.Progress;
        }
        if (_showInfo && _infoRect.Contains(p)) return WidgetPart.Info;
        foreach (var (part, rect) in _buttonRects)
        {
            if (rect.Contains(p)) return part;
        }
        return WidgetPart.None;
    }

    private static Point PointFromLParam(IntPtr lParam)
    {
        long v = lParam.ToInt64();
        return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    private Rectangle PartScreenRect(WidgetPart part)
    {
        RectangleF rect = part == WidgetPart.Info ? _infoRect : _buttonRects.GetValueOrDefault(part);
        if (!GetWindowRect(Handle, out var window)) return Rectangle.Empty;
        return new Rectangle(window.Left + (int)rect.X, window.Top + (int)rect.Y, (int)rect.Width, (int)rect.Height);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;

            case WM_MOUSEMOVE:
                if (!_trackingMouse)
                {
                    TrackMouseLeave(Handle);
                    _trackingMouse = true;
                }
            {
                var p = PointFromLParam(m.LParam);
                _mouseX = p.X;
                if (_seekFraction is not null)
                {
                    _seekFraction = FractionAt(p.X);
                    Render();
                    return;
                }
                var part = HitTest(p);
                if (part == WidgetPart.Progress && _hover == WidgetPart.Progress)
                {
                    Render(); // time under the cursor follows the mouse
                    return;
                }
                SetHover(part);
                return;
            }

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                if (_seekFraction is not null) return; // dragging with capture
                _pressed = WidgetPart.None;
                SetHover(WidgetPart.None);
                return;

            case WM_LBUTTONDOWN:
            {
                var p = PointFromLParam(m.LParam);
                _pressed = HitTest(p);
                HideTip();
                if (_pressed == WidgetPart.Progress)
                {
                    SetCapture(Handle);
                    _seekFraction = FractionAt(p.X);
                }
                Render();
                return;
            }

            case WM_LBUTTONUP:
            {
                if (_seekFraction is float target)
                {
                    _seekFraction = FractionAt(PointFromLParam(m.LParam).X);
                    target = _seekFraction.Value;
                    ReleaseCapture();
                    _pressed = WidgetPart.None;
                    SeekRequested?.Invoke(target);
                    _seekFraction = null;
                    SetHover(HitTest(PointFromLParam(m.LParam)));
                    Render();
                    return;
                }
                var part = HitTest(PointFromLParam(m.LParam));
                bool click = part != WidgetPart.None && part == _pressed;
                _pressed = WidgetPart.None;
                Render();
                if (click) PartClicked?.Invoke(part, PartScreenRect(part));
                return;
            }

            case WM_RBUTTONUP:
            {
                HideTip();
                var p = PointFromLParam(m.LParam);
                GetWindowRect(Handle, out var window);
                ContextMenuRequested?.Invoke(new Point(window.Left + p.X, window.Top + p.Y));
                return;
            }

            case WM_MOUSEWHEEL:
                WheelScrolled?.Invoke((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private void SetHover(WidgetPart part)
    {
        if (part == _hover) return;
        _hover = part;
        HideTip();
        if (part != WidgetPart.None) _hoverTimer.Start();
        Render();
    }

    private void ShowHoverTip()
    {
        _hoverTimer.Stop();
        string? text = _hover switch
        {
            WidgetPart.Info => _media.HasSession
                ? $"{_media.Title}{(string.IsNullOrWhiteSpace(_media.Artist) ? "" : " – " + _media.Artist)}\n{Strings.SourceLabel(_media.SourceName)}"
                : Strings.ClickToStartApp,
            WidgetPart.Progress => Strings.ClickOrDragToSeek,
            WidgetPart.Previous => Strings.Previous,
            WidgetPart.PlayPause => !_media.HasSession ? Strings.StartMusicApp : _media.IsPlaying ? Strings.Pause : Strings.Play,
            WidgetPart.Next => Strings.Next,
            WidgetPart.AddToPlaylist => Strings.AddToPlaylistTip,
            WidgetPart.Volume => _muted ? Strings.Muted : Strings.VolumeTip((int)Math.Round(_volume * 100)),
            _ => null,
        };
        if (text is null) return;

        RectangleF rect = _hover == WidgetPart.Info ? _infoRect : _buttonRects.GetValueOrDefault(_hover);
        try
        {
            // Negative offset: show above the taskbar.
            _toolTip.Show(text, this, (int)rect.X, -(int)(44 * _scale), 4000);
        }
        catch (Exception)
        {
            // Tooltips are a nicety.
        }
    }

    private void HideTip()
    {
        _hoverTimer.Stop();
        try
        {
            _toolTip.Hide(this);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _hoverTimer.Dispose();
        _toolTip.Dispose();
        if (Handle != IntPtr.Zero)
        {
            if (IsWindow(Handle)) DestroyHandle(); else ReleaseHandle();
        }
    }
}
