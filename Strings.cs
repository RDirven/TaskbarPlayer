namespace TaskbarPlayer;

internal enum AppLanguage
{
    English,
    Dutch,
}

/// <summary>All user-visible text, in English (default) and Dutch.</summary>
internal static class Strings
{
    public static AppLanguage Language { get; set; } = AppLanguage.English;

    private static string T(string english, string dutch) => Language == AppLanguage.Dutch ? dutch : english;

    // Widget
    public static string Unknown => T("Unknown", "Onbekend");
    public static string NothingPlaying => T("Nothing playing", "Er speelt niets af");
    public static string ClickToStartApp => T("Click to start a music app", "Klik om een muziek-app te starten");
    public static string MediaUnavailable => T("Media unavailable", "Media niet beschikbaar");
    public static string SourceLabel(string? name) => T($"Source: {name}", $"Bron: {name}");
    public static string ClickOrDragToSeek => T("Click or drag to seek", "Klik of sleep om te spoelen");
    public static string Previous => T("Previous", "Vorige");
    public static string Next => T("Next", "Volgende");
    public static string Play => T("Play", "Afspelen");
    public static string Pause => T("Pause", "Pauzeren");
    public static string StartMusicApp => T("Start a music app", "Muziek-app starten");
    public static string Muted => T("Muted", "Gedempt");
    public static string VolumeTip(int percent) =>
        T($"Volume {percent}%  (scroll to change)", $"Volume {percent}%  (scrollen om te wijzigen)");

    // Menus
    public static string Loading => T("(loading…)", "(laden…)");
    public static string Source => T("Source", "Bron");
    public static string AutomaticSource => T("Automatic (whatever is playing)", "Automatisch (wat er speelt)");
    public static string NoActivePlayers => T("No active media players", "Geen actieve mediaspelers");
    public static string StartApp(string name) => T($"Start {name}", $"{name} starten");
    public static string OpenYouTubeMusicInBrowser => T("Open YouTube Music in browser", "YouTube Music openen in de browser");
    public static string Monitor => T("Display", "Scherm");
    public static string MonitorLabel(string number, int width, int height) =>
        T($"Display {number}  ({width}×{height})", $"Scherm {number}  ({width}×{height})");
    public static string MainDisplay => T(" – main display", " – hoofdscherm");
    public static string EnableTaskbarOnAllDisplays =>
        T("Turn on 'Show my taskbar on all displays' in Settings", "Zet 'Taakbalk op alle beeldschermen weergeven' aan in Instellingen");
    public static string Position => T("Position", "Positie");
    public static string PositionAuto => T("Automatic (where there is most room)", "Automatisch (waar de meeste ruimte is)");
    public static string PositionLeft => T("Left of the app icons", "Links van de app-iconen");
    public static string PositionRight => T("Right of the app icons", "Rechts van de app-iconen");
    public static string ShowCover => T("Show album art", "Albumhoes tonen");
    public static string ShowTitle => T("Show title and artist", "Titel en artiest tonen");
    public static string StartWithWindows => T("Start with Windows", "Starten met Windows");
    public static string LanguageMenu => T("Language", "Taal");

    // YouTube Music playlists
    public static string AddToPlaylistTip => T("Like or add to a YouTube Music playlist", "Liken of toevoegen aan een YouTube Music-afspeellijst");
    public static string LikeSong => T("Like (add to Liked music)", "Liken (toevoegen aan Je likes)");
    public static string ConnectYouTube => T("Connect YouTube account…", "YouTube-account koppelen…");
    public static string DisconnectYouTube => T("Disconnect YouTube account", "YouTube-account ontkoppelen");
    public static string YouTubeAccount => T("YouTube Music account", "YouTube Music-account");
    public static string RefreshPlaylists => T("Refresh playlists", "Afspeellijsten vernieuwen");
    public static string NoPlaylists => T("No playlists found", "Geen afspeellijsten gevonden");
    public static string Searching => T("Looking up track…", "Nummer opzoeken…");
    public static string AddedTo(string playlist) => T($"✓ Added to {playlist}", $"✓ Toegevoegd aan {playlist}");
    public static string Liked => T("✓ Liked", "✓ Geliked");
    public static string NotFoundOnYouTube => T("Track not found on YouTube", "Nummer niet gevonden op YouTube");
    public static string QuotaExceeded => T("YouTube daily quota used up, try again tomorrow", "YouTube-daglimiet bereikt, probeer het morgen opnieuw");
    public static string YouTubeNotConnected => T("YouTube account not connected", "YouTube-account niet gekoppeld");
    public static string YouTubeSignInExpired => T("YouTube sign-in expired, please connect again", "YouTube-login verlopen, koppel opnieuw");
    public static string SignInDonePage => T("Signed in. You can close this tab and return to Taskbar Player.",
        "Ingelogd. Je kunt dit tabblad sluiten en teruggaan naar Taskbar Player.");
    public static string SignInFailedPage => T("Sign-in failed or was cancelled.", "Inloggen mislukt of geannuleerd.");
    public static string YouTubeSetupTitle => T("Connect YouTube Music", "YouTube Music koppelen");
    public static string YouTubeSetupSteps => T(
        "To add songs to your playlists, Taskbar Player uses the YouTube Data API with your own (free) " +
        "Google Cloud project. One-time setup, about 5 minutes:\n\n" +
        "1. Open the Google Cloud Console, create a project and enable \"YouTube Data API v3\".\n" +
        "2. Google Auth Platform → set up the consent screen (External). Under Audience, add yourself " +
        "as a test user, or click \"Publish app\" so you don't have to reconnect every 7 days.\n" +
        "3. Clients → Create client → application type \"Desktop app\".\n" +
        "4. Paste the Client ID and Client secret below and click Connect, then sign in in your browser.",
        "Om nummers aan je afspeellijsten toe te voegen gebruikt Taskbar Player de YouTube Data API met je " +
        "eigen (gratis) Google Cloud-project. Eenmalig, ongeveer 5 minuten:\n\n" +
        "1. Open de Google Cloud Console, maak een project en zet \"YouTube Data API v3\" aan.\n" +
        "2. Google Auth Platform → stel het toestemmingsscherm in (Extern). Voeg jezelf bij Doelgroep toe " +
        "als testgebruiker, of klik \"App publiceren\" zodat je niet elke 7 dagen opnieuw hoeft te koppelen.\n" +
        "3. Clients → Client maken → type \"Desktop-app\".\n" +
        "4. Plak hieronder de Client ID en Client secret, klik Koppelen en log in via je browser.");
    public static string MuSyncHeader => T("Add to Garmin playlist (MuSync)", "Naar Garmin-afspeellijst (MuSync)");
    public static string DownloadingForGarmin => T("Downloading for your Garmin…", "Downloaden voor je Garmin…");
    public static string AddedToMuSync(string playlist) =>
        T($"✓ In '{playlist}', on your watch after the next sync", $"✓ In '{playlist}', na de volgende sync op je horloge");
    public static string OpenGoogleCloudConsole => T("Open Google Cloud Console", "Google Cloud Console openen");
    public static string Connect => T("Connect", "Koppelen");
    public static string Cancel => T("Cancel", "Annuleren");
    public static string EnterClientIdAndSecret => T("Enter both the Client ID and the Client secret.", "Vul zowel de Client ID als het Client secret in.");
    public static string WaitingForBrowser => T("Waiting for sign-in in your browser…", "Wachten op inloggen in je browser…");
    public static string SignInCancelled => T("Sign-in cancelled or timed out.", "Inloggen geannuleerd of verlopen.");
    public static string Exit => T("Exit", "Afsluiten");
}
