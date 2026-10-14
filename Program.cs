namespace TaskbarPlayer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\TaskbarPlayer.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        // There is no window to show an error dialog in; log instead of popping up WinForms' crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"Unhandled: {e.Exception}");
        Application.Run(new TrayApp());
    }
}
