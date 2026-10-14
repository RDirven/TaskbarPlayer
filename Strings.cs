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
    public static string Exit => T("Exit", "Afsluiten");
}
