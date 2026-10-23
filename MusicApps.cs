using System.Diagnostics;

namespace TaskbarPlayer;

internal sealed record MusicApp(string Name, string AppId);

/// <summary>Finds installed music players via the Start menu's app list (shell:AppsFolder) and launches them.</summary>
internal static class MusicApps
{
    public const string YouTubeMusicUrl = "https://music.youtube.com";

    // Start menu names of players that publish media sessions. Matched exactly (case-insensitive).
    private static readonly string[] KnownNames =
    [
        "Spotify", "YouTube Music", "Apple Music", "iTunes", "Deezer", "TIDAL", "Amazon Music", "Qobuz",
        "SoundCloud", "Pandora", "Media Player", "VLC media player", "Sonos", "foobar2000", "MusicBee",
        "AIMP", "Winamp", "Plexamp",
    ];

    private static IReadOnlyList<MusicApp> s_cached = [];
    private static Task? s_scan;

    /// <summary>Last scan result; empty until the first background scan finished.</summary>
    public static IReadOnlyList<MusicApp> Installed => s_cached;

    public static bool HasYouTubeMusicApp =>
        s_cached.Any(a => a.Name.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase));

    /// <summary>Rescans in the background (the Shell API needs an STA thread and can take a moment).</summary>
    public static Task RefreshAsync()
    {
        if (s_scan is { IsCompleted: false }) return s_scan;

        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                s_cached = Scan();
                MuSync.Detect();
            }
            catch (Exception ex)
            {
                Log.Write($"Scanning music apps failed: {ex.Message}");
            }
            tcs.SetResult();
        })
        {
            IsBackground = true,
            Name = "MusicApps scan",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        s_scan = tcs.Task;
        return s_scan;
    }

    private static List<MusicApp> Scan()
    {
        var found = new List<MusicApp>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return found;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic? folder = shell.NameSpace("shell:AppsFolder");
        if (folder is null) return found;

        foreach (dynamic item in folder.Items())
        {
            string name = item.Name;
            string appId = item.Path;
            if (string.IsNullOrEmpty(appId)) continue;
            if (!KnownNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            if (found.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(new MusicApp(name, appId));
        }

        // Keep the order of KnownNames: the most likely choices first.
        return found
            .OrderBy(a => Array.FindIndex(KnownNames, n => n.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static void Launch(MusicApp app) =>
        Start("explorer.exe", $"shell:AppsFolder\\{app.AppId}");

    public static void OpenYouTubeMusicInBrowser() => Start(YouTubeMusicUrl, null);

    private static void Start(string fileName, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments ?? "") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Launching {fileName} {arguments} failed: {ex.Message}");
        }
    }
}
