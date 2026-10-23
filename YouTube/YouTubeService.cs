using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskbarPlayer.YouTube;

internal sealed record YouTubePlaylist(string Id, string Title);

internal sealed record YouTubeVideo(string Id, string Title, string Channel);

internal sealed class YouTubeException(string message, string? reason = null) : Exception(message)
{
    public string? Reason { get; } = reason;
    public bool IsQuotaExceeded => Reason is "quotaExceeded" or "dailyLimitExceeded";
}

/// <summary>
/// Talks to the YouTube Data API v3 with the user's own OAuth client (YouTube Music playlists are YouTube
/// playlists, and "liked music" is the user's liked videos). Credentials and the refresh token are stored
/// encrypted with DPAPI for the current Windows user.
/// </summary>
internal sealed class YouTubeService : IDisposable
{
    private const string Scope = "https://www.googleapis.com/auth/youtube";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string ApiBase = "https://www.googleapis.com/youtube/v3/";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarPlayer", "youtube.dat");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, YouTubeVideo> _matches = new(StringComparer.OrdinalIgnoreCase);
    private Credentials? _credentials;
    private string? _accessToken;
    private DateTime _accessTokenExpires;
    private List<YouTubePlaylist>? _playlists;
    private DateTime _playlistsLoaded;

    private sealed class Credentials
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string? RefreshToken { get; set; }
    }

    public bool IsConnected => !string.IsNullOrEmpty(_credentials?.RefreshToken);

    /// <summary>Client id from an earlier connection, to prefill the setup dialog.</summary>
    public string? SavedClientId => _credentials?.ClientId;

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            _credentials = JsonSerializer.Deserialize<Credentials>(plain);
        }
        catch (Exception ex)
        {
            Log.Write($"Loading YouTube credentials failed: {ex.Message}");
            _credentials = null;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_credentials);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    // ---------------------------------------------------------------- sign-in

    /// <summary>
    /// Runs the OAuth "installed app" flow: opens the browser, receives the code on a loopback port (PKCE),
    /// and exchanges it for tokens.
    /// </summary>
    public async Task ConnectAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        string redirectUri = $"http://127.0.0.1:{GetFreePort()}/";
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        string authUrl = AuthEndpoint + "?" + Query(
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", Scope),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            ("access_type", "offline"),
            ("prompt", "consent"));
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        _ = contextTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token));
        if (finished != contextTask) throw new OperationCanceledException();

        var context = await contextTask;
        var query = context.Request.QueryString;
        string? error = query["error"];
        string? code = query["code"];
        bool ok = error is null && code is not null && query["state"] == state;

        byte[] page = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=utf-8><title>Taskbar Player</title>" +
            $"<body style=\"font-family:Segoe UI,sans-serif;padding:40px\"><h2>Taskbar Player</h2>" +
            $"<p>{WebUtility.HtmlEncode(ok ? Strings.SignInDonePage : Strings.SignInFailedPage)}</p></body>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, cancellationToken);
        context.Response.Close();

        if (!ok) throw new YouTubeException(error ?? Strings.SignInFailedPage);

        var tokens = await PostFormAsync(TokenEndpoint,
            ("code", code!),
            ("client_id", clientId),
            ("client_secret", clientSecret),
            ("redirect_uri", redirectUri),
            ("code_verifier", verifier),
            ("grant_type", "authorization_code"));

        string? refreshToken = tokens["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refreshToken)) throw new YouTubeException("Google did not return a refresh token.");

        _credentials = new Credentials { ClientId = clientId, ClientSecret = clientSecret, RefreshToken = refreshToken };
        StoreAccessToken(tokens);
        _playlists = null;
        Save();
    }

    public async Task DisconnectAsync()
    {
        string? token = _credentials?.RefreshToken;
        _credentials = null;
        _accessToken = null;
        _playlists = null;
        _matches.Clear();
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception)
        {
            // Nothing sensible to do.
        }

        if (token is null) return;
        try
        {
            // Best effort: also revoke the grant at Google.
            await _http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent([new("token", token)]));
        }
        catch (Exception)
        {
        }
    }

    private async Task<string> GetAccessTokenAsync()
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpires) return _accessToken;
        if (_credentials?.RefreshToken is not string refreshToken) throw new YouTubeException(Strings.YouTubeNotConnected);

        try
        {
            var tokens = await PostFormAsync(TokenEndpoint,
                ("client_id", _credentials.ClientId),
                ("client_secret", _credentials.ClientSecret),
                ("refresh_token", refreshToken),
                ("grant_type", "refresh_token"));
            StoreAccessToken(tokens);
            return _accessToken!;
        }
        catch (YouTubeException ex) when (ex.Reason == "invalid_grant")
        {
            // Revoked, or expired (apps left in "Testing" get 7-day refresh tokens): ask to sign in again.
            _credentials.RefreshToken = null;
            Save();
            throw new YouTubeException(Strings.YouTubeSignInExpired, ex.Reason);
        }
    }

    private void StoreAccessToken(JsonNode tokens)
    {
        _accessToken = tokens["access_token"]?.GetValue<string>();
        int expiresIn = tokens["expires_in"]?.GetValue<int>() ?? 3600;
        _accessTokenExpires = DateTime.UtcNow.AddSeconds(expiresIn - 60);
    }

    // ---------------------------------------------------------------- API

    public async Task<IReadOnlyList<YouTubePlaylist>> GetPlaylistsAsync(bool refresh = false)
    {
        if (!refresh && _playlists is not null && DateTime.UtcNow - _playlistsLoaded < TimeSpan.FromMinutes(10))
        {
            return _playlists;
        }

        var result = new List<YouTubePlaylist>();
        string? pageToken = null;
        for (int page = 0; page < 5; page++)
        {
            string url = "playlists?part=snippet&mine=true&maxResults=50" + (pageToken is null ? "" : "&pageToken=" + pageToken);
            var json = await SendAsync(HttpMethod.Get, url);
            foreach (var item in json["items"]?.AsArray() ?? [])
            {
                string? id = item?["id"]?.GetValue<string>();
                string? title = item?["snippet"]?["title"]?.GetValue<string>();
                if (id is not null) result.Add(new YouTubePlaylist(id, title ?? id));
            }
            pageToken = json["nextPageToken"]?.GetValue<string>();
            if (pageToken is null) break;
        }

        _playlists = result.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        _playlistsLoaded = DateTime.UtcNow;
        return _playlists;
    }

    /// <summary>
    /// Finds the YouTube video for a track. Media sessions don't expose video ids, so this searches by
    /// title and artist and prefers the "Artist - Topic" channel, which hosts the tracks YouTube Music plays.
    /// </summary>
    public async Task<YouTubeVideo?> FindVideoAsync(string title, string? artist, bool musicOnly)
    {
        string key = $"{artist}\n{title}";
        if (_matches.TryGetValue(key, out var cached)) return cached;

        string q = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
        var candidates = await SearchAsync(q, musicOnly);
        if (candidates.Count == 0 && musicOnly) candidates = await SearchAsync(q, musicOnly: false);
        if (candidates.Count == 0) return null;

        YouTubeVideo? best = null;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            best = candidates.FirstOrDefault(v => v.Channel.Equals($"{artist} - Topic", StringComparison.OrdinalIgnoreCase));
        }
        best ??= candidates.FirstOrDefault(v =>
            v.Channel.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) &&
            v.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        best ??= candidates[0];

        _matches[key] = best;
        return best;
    }

    private async Task<List<YouTubeVideo>> SearchAsync(string q, bool musicOnly)
    {
        string url = "search?part=snippet&type=video&maxResults=5&q=" + Uri.EscapeDataString(q) +
                     (musicOnly ? "&videoCategoryId=10" : "");
        var json = await SendAsync(HttpMethod.Get, url);
        var videos = new List<YouTubeVideo>();
        foreach (var item in json["items"]?.AsArray() ?? [])
        {
            string? id = item?["id"]?["videoId"]?.GetValue<string>();
            if (id is null) continue;
            string title = WebUtility.HtmlDecode(item?["snippet"]?["title"]?.GetValue<string>() ?? "");
            string channel = WebUtility.HtmlDecode(item?["snippet"]?["channelTitle"]?.GetValue<string>() ?? "");
            videos.Add(new YouTubeVideo(id, title, channel));
        }
        return videos;
    }

    public Task AddToPlaylistAsync(string playlistId, string videoId)
    {
        var body = new JsonObject
        {
            ["snippet"] = new JsonObject
            {
                ["playlistId"] = playlistId,
                ["resourceId"] = new JsonObject { ["kind"] = "youtube#video", ["videoId"] = videoId },
            },
        };
        return SendAsync(HttpMethod.Post, "playlistItems?part=snippet", body);
    }

    public Task LikeAsync(string videoId) =>
        SendAsync(HttpMethod.Post, $"videos/rate?id={Uri.EscapeDataString(videoId)}&rating=like");

    // ---------------------------------------------------------------- HTTP helpers

    private async Task<JsonNode> SendAsync(HttpMethod method, string relativeUrl, JsonNode? body = null, bool retried = false)
    {
        using var request = new HttpRequestMessage(method, ApiBase + relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync());
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        else if (method == HttpMethod.Post)
        {
            request.Content = new ByteArrayContent([]);
        }

        using var response = await _http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.Unauthorized && !retried)
        {
            _accessToken = null;
            return await SendAsync(method, relativeUrl, body, retried: true);
        }
        if (!response.IsSuccessStatusCode)
        {
            var error = TryParse(text)?["error"];
            string message = error?["message"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
            string? reason = error?["errors"]?[0]?["reason"]?.GetValue<string>();
            throw new YouTubeException(message, reason);
        }
        return TryParse(text) ?? new JsonObject();
    }

    private async Task<JsonNode> PostFormAsync(string url, params (string Key, string Value)[] fields)
    {
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var response = await _http.PostAsync(url, content);
        string text = await response.Content.ReadAsStringAsync();
        var json = TryParse(text);
        if (!response.IsSuccessStatusCode || json is null)
        {
            string? reason = json?["error"]?.GetValue<string>();
            string message = json?["error_description"]?.GetValue<string>() ?? reason ?? $"{(int)response.StatusCode}";
            throw new YouTubeException(message, reason);
        }
        return json;
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Query(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => _http.Dispose();
}
