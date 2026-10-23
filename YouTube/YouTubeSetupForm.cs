using System.Diagnostics;

namespace TaskbarPlayer.YouTube;

/// <summary>One-time setup: the user pastes their own Google OAuth client and signs in through the browser.</summary>
internal sealed class YouTubeSetupForm : Form
{
    private const string ConsoleUrl = "https://console.cloud.google.com/apis/library/youtube.googleapis.com";

    private readonly YouTubeService _service;
    private readonly TextBox _clientId;
    private readonly TextBox _clientSecret;
    private readonly Button _connect;
    private readonly Label _status;
    private CancellationTokenSource? _signIn;

    public YouTubeSetupForm(YouTubeService service)
    {
        _service = service;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.YouTubeSetupTitle;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 400);
        TopMost = true;

        var intro = new Label
        {
            Location = new Point(16, 14),
            Size = new Size(488, 200),
            Text = Strings.YouTubeSetupSteps,
        };

        var link = new LinkLabel
        {
            Location = new Point(16, 214),
            AutoSize = true,
            Text = Strings.OpenGoogleCloudConsole,
        };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(ConsoleUrl) { UseShellExecute = true });

        var idLabel = new Label { Location = new Point(16, 248), AutoSize = true, Text = "Client ID" };
        _clientId = new TextBox { Location = new Point(130, 245), Size = new Size(374, 26), Text = service.SavedClientId ?? "" };
        var secretLabel = new Label { Location = new Point(16, 284), AutoSize = true, Text = "Client secret" };
        _clientSecret = new TextBox { Location = new Point(130, 281), Size = new Size(374, 26), UseSystemPasswordChar = true };

        _status = new Label
        {
            Location = new Point(16, 320),
            Size = new Size(488, 36),
            ForeColor = Color.DimGray,
        };

        _connect = new Button { Location = new Point(318, 360), Size = new Size(100, 30), Text = Strings.Connect };
        _connect.Click += async (_, _) => await ConnectAsync();
        var cancel = new Button { Location = new Point(424, 360), Size = new Size(80, 30), Text = Strings.Cancel, DialogResult = DialogResult.Cancel };

        AcceptButton = _connect;
        CancelButton = cancel;
        Controls.AddRange([intro, link, idLabel, _clientId, secretLabel, _clientSecret, _status, _connect, cancel]);
        ResumeLayout(performLayout: true);
    }

    private async Task ConnectAsync()
    {
        string id = _clientId.Text.Trim();
        string secret = _clientSecret.Text.Trim();
        if (id.Length == 0 || secret.Length == 0)
        {
            _status.Text = Strings.EnterClientIdAndSecret;
            return;
        }

        _connect.Enabled = false;
        _status.Text = Strings.WaitingForBrowser;
        _signIn = new CancellationTokenSource();
        try
        {
            await _service.ConnectAsync(id, secret, _signIn.Token);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            _status.Text = Strings.SignInCancelled;
        }
        catch (Exception ex)
        {
            Log.Write($"YouTube sign-in failed: {ex}");
            _status.Text = ex.Message;
        }
        finally
        {
            _connect.Enabled = true;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _signIn?.Cancel();
        _signIn?.Dispose();
        base.OnFormClosed(e);
    }
}
