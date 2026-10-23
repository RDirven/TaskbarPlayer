using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace TaskbarPlayer;

/// <summary>Icon glyphs from the Windows icon font plus a few drawing helpers.</summary>
internal static class Glyphs
{
    public const string Play = "";
    public const string Pause = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Mute = "";
    public const string Volume1 = "";
    public const string Volume2 = "";
    public const string Volume3 = "";
    public const string MusicNote = "";
    public const string AddToPlaylist = ""; // "Add" (+)
    public const string Heart = "";

    public static readonly string FontFamilyName =
        IsFontInstalled("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static string ForVolume(float level, bool muted) => muted || level <= 0.001f ? Mute
        : level < 0.34f ? Volume1
        : level < 0.67f ? Volume2
        : Volume3;

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>True when the taskbar uses the light theme.</summary>
    public static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    private static bool IsFontInstalled(string name)
    {
        using var font = new Font(name, 10f);
        return string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase);
    }
}
