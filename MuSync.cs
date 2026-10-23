using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskbarPlayer;

internal sealed record MuSyncResult(bool Ok, string? Message, string? Title);

/// <summary>
/// Integration with MuSync (github.com/RDirven/MuSync), which downloads YouTube tracks and syncs playlists to a
/// Garmin watch. Tracks are added through its <c>pc/musync_cli.py add</c> command; the next sync puts them on
/// the watch.
/// </summary>
internal static class MuSync
{
    public const string SinglesPlaylist = "Losse nummers";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuSync", "config.json");

    private static readonly string ShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MuSync.lnk");

    private static string? s_python;
    private static string? s_cli;

    public static bool IsAvailable => s_python is not null && s_cli is not null;

    /// <summary>Finds MuSync through its Start menu shortcut (pythonw.exe "...\pc\musync_pc.py").</summary>
    public static void Detect()
    {
        try
        {
            if (!File.Exists(ShortcutPath)) return;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(ShortcutPath);
            string target = link.TargetPath;
            string arguments = link.Arguments;

            string script = arguments.Trim().Trim('"');
            string? pcDir = Path.GetDirectoryName(script);
            if (pcDir is null) return;
            string cli = Path.Combine(pcDir, "musync_cli.py");
            if (!File.Exists(cli)) return; // older MuSync without the command line interface

            // python.exe (console) next to pythonw.exe, so stdout can be read; run without a window.
            string python = Path.Combine(Path.GetDirectoryName(target) ?? "", "python.exe");
            s_python = File.Exists(python) ? python : target;
            s_cli = cli;
            Log.Write($"MuSync found: {s_cli}");
        }
        catch (Exception ex)
        {
            Log.Write($"Detecting MuSync failed: {ex.Message}");
        }
    }

    /// <summary>Playlist names from MuSync's config (read-only).</summary>
    public static IReadOnlyList<string> GetPlaylists()
    {
        var names = new List<string>();
        try
        {
            using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var json = JsonNode.Parse(stream);
            foreach (var p in json?["playlists"]?.AsArray() ?? [])
            {
                if (p?["name"]?.GetValue<string>() is string name && !string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Reading MuSync playlists failed: {ex.Message}");
        }
        if (!names.Contains(SinglesPlaylist)) names.Add(SinglesPlaylist);
        return names;
    }

    public static async Task<MuSyncResult> AddAsync(string title, string? artist, string playlist)
    {
        if (!IsAvailable) return new MuSyncResult(false, "MuSync not found", null);

        var psi = new ProcessStartInfo(s_python!)
        {
            WorkingDirectory = Path.GetDirectoryName(s_cli)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add(s_cli!);
        psi.ArgumentList.Add("add");
        psi.ArgumentList.Add("--query");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");
        psi.ArgumentList.Add("--title");
        psi.ArgumentList.Add(title);
        if (!string.IsNullOrWhiteSpace(artist))
        {
            psi.ArgumentList.Add("--artist");
            psi.ArgumentList.Add(artist);
        }
        psi.ArgumentList.Add("--playlist");
        psi.ArgumentList.Add(playlist);

        using var process = Process.Start(psi);
        if (process is null) return new MuSyncResult(false, "Could not start MuSync", null);

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new MuSyncResult(false, "MuSync timed out", null);
        }

        string output = await stdout;
        string progress = await stderr;
        Log.Write($"MuSync add '{artist} - {title}' -> {playlist}: exit {process.ExitCode}\n{progress.Trim()}\n{output.Trim()}");

        string? lastLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        try
        {
            var json = lastLine is null ? null : JsonNode.Parse(lastLine);
            if (json?["ok"]?.GetValue<bool>() == true)
            {
                return new MuSyncResult(true, null, json["title"]?.GetValue<string>());
            }
            return new MuSyncResult(false, json?["error"]?.GetValue<string>() ?? $"MuSync exit code {process.ExitCode}", null);
        }
        catch (Exception)
        {
            return new MuSyncResult(false, $"MuSync exit code {process.ExitCode}", null);
        }
    }
}
