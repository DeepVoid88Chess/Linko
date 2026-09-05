using System.Drawing;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

const long JpegQuality = 55L;
const int FrameIntervalMs = 100;

// Set this to the public Linko Worker/site URL once it is deployed.
const string LinkoServerUrl = "https://YOUR-LINKO-SERVER.example";

ApplicationConfiguration.Initialize();
Application.Run(new LinkoForm());

public sealed class LinkoForm : Form
{
    private readonly Label codeLabel;
    private readonly Label statusLabel;
    private readonly Button approveButton;
    private readonly Button stopButton;
    private ClientWebSocket? socket;
    private CancellationTokenSource? cts;

    public LinkoForm()
    {
        Text = "Linko";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 330);
        MinimumSize = new Size(460, 330);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(18, 18, 20);
        ForeColor = Color.White;

        var title = new Label
        {
            Text = "Linko",
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(32, 28)
        };

        var subtitle = new Label
        {
            Text = "Your computer. Wherever you are.",
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            ForeColor = Color.Silver,
            Location = new Point(35, 72)
        };

        var codeTitle = new Label
        {
            Text = "Your pairing code",
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            ForeColor = Color.Silver,
            Location = new Point(35, 115)
        };

        codeLabel = new Label
        {
            Text = "------",
            Font = new Font("Segoe UI", 32, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(32, 135)
        };

        statusLabel = new Label
        {
            Text = "Starting Linko…",
            Font = new Font("Segoe UI", 10),
            AutoSize = false,
            Size = new Size(390, 42),
            ForeColor = Color.Silver,
            Location = new Point(35, 190)
        };

        approveButton = new Button
        {
            Text = "Allow remote control",
            Size = new Size(185, 42),
            Location = new Point(35, 245),
            BackColor = Color.White,
            ForeColor = Color.Black,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        approveButton.Click += async (_, _) => await ApproveAsync();

        stopButton = new Button
        {
            Text = "Stop",
            Size = new Size(100, 42),
            Location = new Point(230, 245),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        stopButton.Click += (_, _) => StopLinko();

        Controls.AddRange([title, subtitle, codeTitle, codeLabel, statusLabel, approveButton, stopButton]);
        Shown += async (_, _) => await StartPairingAsync();
        FormClosing += (_, _) => StopLinko();
    }

    private async Task StartPairingAsync()
    {
        try
        {
            if (!Uri.TryCreate(LinkoServerUrl, UriKind.Absolute, out var baseUri))
                throw new InvalidOperationException("Linko's server address has not been configured yet.");

            using var http = new HttpClient();
            using var response = await http.PostAsync(new Uri(baseUri, "/api/pair"), null);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var pairingCode = json.RootElement.GetProperty("pairingCode").GetString() ?? "------";

            codeLabel.Text = pairingCode.Insert(3, " ");
            statusLabel.Text = "Enter this code on your iPad, then approve remote control here.";
            approveButton.Enabled = true;
            approveButton.Focus();

            Tag = baseUri;
            PairingCode = pairingCode;
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"Could not connect to Linko: {ex.Message}";
            approveButton.Enabled = false;
        }
    }

    private string? PairingCode { get; set; }

    private async Task ApproveAsync()
    {
        if (Tag is not Uri baseUri || string.IsNullOrWhiteSpace(PairingCode)) return;

        approveButton.Enabled = false;
        statusLabel.Text = "Connecting…";

        try
        {
            socket = new ClientWebSocket();
            var wsScheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            var wsUri = new Uri($"{wsScheme}://{baseUri.Authority}/ws?code={PairingCode}");
            await socket.ConnectAsync(wsUri, CancellationToken.None);

            cts = new CancellationTokenSource();
            statusLabel.Text = "Connected. Remote control is active.";

            var receiver = ReceiveCommandsAsync(socket, cts.Token);
            var sender = SendFramesAsync(socket, cts.Token);
            await Task.WhenAny(receiver, sender);
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"Connection failed: {ex.Message}";
        }
    }

    private void StopLinko()
    {
        try { cts?.Cancel(); } catch { }
        try { socket?.Dispose(); } catch { }
        socket = null;
        statusLabel.Text = "Linko stopped.";
        approveButton.Enabled = false;
    }

    private static async Task SendFramesAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                using var bitmap = CaptureScreen();
                using var stream = new MemoryStream();
                var encoder = GetJpegEncoder();
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
                bitmap.Save(stream, encoder, parameters);
                await socket.SendAsync(new ArraySegment<byte>(stream.ToArray()), WebSocketMessageType.Binary, true, token);
                await Task.Delay(FrameIntervalMs, token);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, token); }
        }
    }

    private static async Task ReceiveCommandsAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.Count > 0) message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                    HandleCommand(Encoding.UTF8.GetString(message.ToArray()));
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private static void HandleCommand(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "mouse", StringComparison.OrdinalIgnoreCase)) return;

            var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
            var x = root.TryGetProperty("x", out var xElement) ? xElement.GetDouble() : 0.5;
            var y = root.TryGetProperty("y", out var yElement) ? yElement.GetDouble() : 0.5;
            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);

            var bounds = SystemInformation.VirtualScreen;
            Cursor.Position = new Point(
                bounds.Left + (int)Math.Round(x * Math.Max(0, bounds.Width - 1)),
                bounds.Top + (int)Math.Round(y * Math.Max(0, bounds.Height - 1)));

            if (action == "down") MouseButton(true);
            else if (action == "up") MouseButton(false);
        }
        catch { }
    }

    private static void MouseButton(bool down)
    {
        const uint LeftDown = 0x0002;
        const uint LeftUp = 0x0004;
        mouse_event(down ? LeftDown : LeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static Bitmap CaptureScreen()
    {
        var bounds = SystemInformation.VirtualScreen;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static ImageCodecInfo GetJpegEncoder() =>
        ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}
