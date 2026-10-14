using Microsoft.Win32;
using TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>Windowless application: owns the taskbar widget, the media controller and the menus.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const float WheelVolumeStep = 0.02f;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Control _invoker = new();
    private readonly MediaController _media;
    private readonly TaskbarWidget _widget;
    private readonly MessageWindow _messages = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private ContextMenuStrip _menu = new();
    private VolumePopup? _volumePopup;
    private float _volume;
    private bool _muted;

    public TrayApp()
    {
        _ = _invoker.Handle; // gives the media controller a UI-thread marshalling target
        Strings.Language = _settings.Language;

        _media = new MediaController(_invoker);
        _media.SetPreferredApp(_settings.PreferredSource);

        _widget = new TaskbarWidget(_media, _settings);
        _widget.PartClicked += OnPartClicked;
        _widget.ContextMenuRequested += p => ShowMenu(_menu, p);
        _widget.WheelScrolled += delta => ChangeVolume(Math.Sign(delta) * WheelVolumeStep);
        _widget.SeekRequested += fraction =>
            RunAsync(() => _media.SeekAsync(TimeSpan.FromTicks((long)(_media.Duration.Ticks * (double)fraction))));
        _media.Changed += (_, _) => _widget.Render();

        // Explorer restarted: our child window died with the old taskbar.
        _messages.TaskbarCreated += () => _widget.Attach();

        BuildMenu();
        RefreshVolume();
        _widget.Attach();

        _timer.Tick += (_, _) =>
        {
            RefreshVolume();
            _widget.Tick();
        };
        _timer.Start();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _ = InitializeMediaAsync();
        _ = MusicApps.RefreshAsync();
    }

    private async Task InitializeMediaAsync()
    {
        try
        {
            await _media.InitializeAsync();
        }
        catch (Exception)
        {
            _widget.StatusText = Strings.MediaUnavailable;
            _widget.Render();
        }
    }

    private void OnPartClicked(WidgetPart part, Rectangle screenRect)
    {
        switch (part)
        {
            case WidgetPart.Previous: RunAsync(_media.PreviousAsync); break;
            case WidgetPart.PlayPause:
                // Nothing to play/pause: offer to start a music app instead.
                if (_media.HasSession) RunAsync(_media.PlayPauseAsync);
                else ShowPopupMenu(FillLaunchItems, screenRect);
                break;
            case WidgetPart.Next: RunAsync(_media.NextAsync); break;
            case WidgetPart.Volume: ToggleVolumePopup(screenRect); break;
            case WidgetPart.Info:
                ShowPopupMenu(_media.HasSession ? FillSourceItems : FillLaunchItems, screenRect);
                break;
        }
    }

    private void ShowPopupMenu(Action<ToolStripItemCollection> fill, Rectangle anchor)
    {
        var menu = new ContextMenuStrip();
        fill(menu.Items);
        menu.Closed += (_, _) => _invoker.BeginInvoke(new Action(menu.Dispose));
        ShowMenu(menu, new Point(anchor.Left, anchor.Top));
    }

    private void FillLaunchItems(ToolStripItemCollection items)
    {
        items.Clear();
        foreach (var app in MusicApps.Installed)
        {
            items.Add(new ToolStripMenuItem(Strings.StartApp(app.Name), null, (_, _) => MusicApps.Launch(app)));
        }
        if (!MusicApps.HasYouTubeMusicApp)
        {
            items.Add(new ToolStripMenuItem(Strings.OpenYouTubeMusicInBrowser, null, (_, _) => MusicApps.OpenYouTubeMusicInBrowser()));
        }
        // Pick up apps installed since the last scan, for next time.
        _ = MusicApps.RefreshAsync();
    }

    // ---------------------------------------------------------------- volume

    private void RefreshVolume()
    {
        if (SystemVolume.Get() is not (float level, bool muted)) return;
        _volume = level;
        _muted = muted;
        _widget.SetVolume(level, muted);
        _volumePopup?.SetState(level, muted);
    }

    private void ChangeVolume(float delta)
    {
        if (SystemVolume.Get() is not (float level, bool muted)) return;
        if (muted && delta > 0) SystemVolume.SetMuted(false);
        SystemVolume.SetLevel(level + delta);
        RefreshVolume();
    }

    private void ToggleVolumePopup(Rectangle anchor)
    {
        bool light = Glyphs.IsLightTaskbar();
        if (_volumePopup is not null && _volumePopup.IsLight != light)
        {
            _volumePopup.Dispose();
            _volumePopup = null;
        }

        if (_volumePopup is null)
        {
            _volumePopup = new VolumePopup(light);
            _volumePopup.VolumeChanged += level =>
            {
                if (_muted && level > 0) SystemVolume.SetMuted(false);
                SystemVolume.SetLevel(level);
                RefreshVolume();
            };
            _volumePopup.MuteToggled += () =>
            {
                SystemVolume.SetMuted(!_muted);
                RefreshVolume();
            };
        }

        if (_volumePopup.Visible)
        {
            _volumePopup.HideNow();
            return;
        }
        // Clicking the button deactivates (and hides) the popup first; treat that click as "close".
        if ((DateTime.UtcNow - _volumePopup.HiddenAt).TotalMilliseconds < 300) return;

        _volumePopup.SetState(_volume, _muted);
        _volumePopup.ShowAbove(anchor, _widget.TrayScreenBounds);
    }

    // ---------------------------------------------------------------- menus

    private void BuildMenu()
    {
        _menu.Dispose();
        _menu = new ContextMenuStrip();

        var source = new ToolStripMenuItem(Strings.Source);
        source.DropDownItems.Add(Strings.Loading);
        source.DropDownOpening += (_, _) => FillSourceItems(source.DropDownItems);

        var placement = new ToolStripMenuItem(Strings.Position);
        var auto = new ToolStripMenuItem(Strings.PositionAuto);
        var left = new ToolStripMenuItem(Strings.PositionLeft);
        var right = new ToolStripMenuItem(Strings.PositionRight);
        auto.Click += (_, _) => SetPlacement(WidgetPlacement.Auto);
        left.Click += (_, _) => SetPlacement(WidgetPlacement.Left);
        right.Click += (_, _) => SetPlacement(WidgetPlacement.Right);
        placement.DropDownItems.AddRange([auto, left, right]);
        placement.DropDownOpening += (_, _) =>
        {
            auto.Checked = _settings.Placement == WidgetPlacement.Auto;
            right.Checked = _settings.Placement == WidgetPlacement.Right;
            left.Checked = _settings.Placement == WidgetPlacement.Left;
        };

        var monitor = new ToolStripMenuItem(Strings.Monitor);
        monitor.DropDownItems.Add(Strings.Loading);
        monitor.DropDownOpening += (_, _) => FillMonitorItems(monitor.DropDownItems);

        var showCover = new ToolStripMenuItem(Strings.ShowCover) { Checked = _settings.ShowCover, CheckOnClick = true };
        showCover.CheckedChanged += (_, _) =>
        {
            _settings.ShowCover = showCover.Checked;
            SaveAndRelayout();
        };

        var showTitle = new ToolStripMenuItem(Strings.ShowTitle) { Checked = _settings.ShowTitle, CheckOnClick = true };
        showTitle.CheckedChanged += (_, _) =>
        {
            _settings.ShowTitle = showTitle.Checked;
            SaveAndRelayout();
        };

        var autostart = new ToolStripMenuItem(Strings.StartWithWindows) { CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            if (autostart.Checked != Autostart.IsEnabled) Autostart.Set(autostart.Checked);
        };
        _menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;

        // Language names are shown in their own language.
        var language = new ToolStripMenuItem(Strings.LanguageMenu);
        foreach (var (lang, name) in new[] { (AppLanguage.English, "English"), (AppLanguage.Dutch, "Nederlands") })
        {
            language.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SetLanguage(lang)) { Checked = Strings.Language == lang });
        }

        _menu.Items.AddRange(
        [
            source,
            monitor,
            placement,
            new ToolStripSeparator(),
            showCover,
            showTitle,
            autostart,
            language,
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.Exit, null, (_, _) => ExitThread()),
        ]);
    }

    private void FillSourceItems(ToolStripItemCollection items)
    {
        items.Clear();

        var auto = new ToolStripMenuItem(Strings.AutomaticSource) { Checked = _settings.PreferredSource is null };
        auto.Click += (_, _) => SelectSource(null);
        items.Add(auto);
        items.Add(new ToolStripSeparator());

        var sources = _media.GetSources();
        if (sources.Count == 0)
        {
            items.Add(new ToolStripMenuItem(Strings.NoActivePlayers) { Enabled = false });
        }
        foreach (var s in sources)
        {
            bool active = string.Equals(s.AppId, _media.SourceAppId, StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem(active ? $"{s.Name}  ●" : s.Name)
            {
                Checked = string.Equals(s.AppId, _settings.PreferredSource, StringComparison.OrdinalIgnoreCase),
                ToolTipText = s.AppId,
            };
            item.Click += (_, _) => SelectSource(s.AppId);
            items.Add(item);
        }

        var launch = new ToolStripMenuItem(Strings.StartMusicApp);
        launch.DropDownItems.Add(Strings.Loading);
        launch.DropDownOpening += (_, _) => FillLaunchItems(launch.DropDownItems);
        items.Add(new ToolStripSeparator());
        items.Add(launch);
    }

    private static void ShowMenu(ContextMenuStrip menu, Point screenPoint) =>
        menu.Show(screenPoint, ToolStripDropDownDirection.AboveRight);

    private void SelectSource(string? appId)
    {
        _settings.PreferredSource = appId;
        _settings.Save();
        _media.SetPreferredApp(appId);
    }

    private void FillMonitorItems(ToolStripItemCollection items)
    {
        items.Clear();
        var bars = TaskbarWidget.FindTaskbars().OrderBy(b => b.Screen.Bounds.Left).ToList();
        foreach (var (_, screen) in bars)
        {
            string number = new string(screen.DeviceName.Where(char.IsDigit).ToArray());
            string label = Strings.MonitorLabel(number, screen.Bounds.Width, screen.Bounds.Height);
            if (screen.Primary) label += Strings.MainDisplay;

            bool selected = _settings.Monitor is string device
                ? string.Equals(device, screen.DeviceName, StringComparison.OrdinalIgnoreCase)
                : screen.Primary;
            var item = new ToolStripMenuItem(label) { Checked = selected };
            string? target = screen.Primary ? null : screen.DeviceName;
            item.Click += (_, _) =>
            {
                _settings.Monitor = target;
                _settings.Save();
                _widget.Tick(); // moves the widget to the other taskbar right away
            };
            items.Add(item);
        }

        if (bars.Count <= 1)
        {
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem(Strings.EnableTaskbarOnAllDisplays) { Enabled = false });
        }
    }

    private void SetLanguage(AppLanguage language)
    {
        Strings.Language = language;
        _settings.Language = language;
        _settings.Save();
        BuildMenu(); // menu texts are created once, so rebuild them
        _widget.Render();
    }

    private void SetPlacement(WidgetPlacement placement)
    {
        _settings.Placement = placement;
        SaveAndRelayout();
    }

    private void SaveAndRelayout()
    {
        _settings.Save();
        _widget.UpdateLayout();
        _widget.Render();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Light/dark switch or DPI change: redraw on the UI thread.
        _invoker.BeginInvoke(new Action(() =>
        {
            _widget.UpdateLayout();
            _widget.Render();
        }));
    }

    private static async void RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // Media apps come and go; a failed command is not worth a crash.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _timer.Dispose();
            _widget.Dispose();
            _media.Dispose();
            _menu.Dispose();
            _volumePopup?.Dispose();
            _messages.DestroyHandle();
            _invoker.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Hidden top-level window that receives the "TaskbarCreated" broadcast.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private static readonly int WM_TaskbarCreated = (int)NativeMethods.RegisterWindowMessage("TaskbarCreated");

        public MessageWindow() => CreateHandle(new CreateParams { Caption = "TaskbarPlayer.Messages" });

        public event Action? TaskbarCreated;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TaskbarCreated) TaskbarCreated?.Invoke();
            base.WndProc(ref m);
        }
    }
}
