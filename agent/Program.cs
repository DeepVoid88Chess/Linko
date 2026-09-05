using System.Drawing;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

const int FrameIntervalMs = 100;
const long JpegQuality = 55L;

Console.Title = "Linko";
Console.WriteLine("Linko by Donaro Inc.");
Console.WriteLine("Your computer. Wherever you are.");
Console.WriteLine();
Console.Write("Linko server URL: ");
var serverUrl = (Console.ReadLine() ?? "").Trim().TrimEnd('/');

if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri) ||
    (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
{
    Console.WriteLine("Please enter a valid http:// or https:// server URL.");
    return;
}

try
{
    using var http = new HttpClient();
    using var response = await http.PostAsync(new Uri(baseUri, "/api/pair"), null);
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var pairingCode = json.RootElement.GetProperty("pairingCode").GetString();

    Console.WriteLine();
    Console.WriteLine($"PAIRING CODE: {pairingCode}");
    Console.WriteLine("Open Linko on your iPad and enter this code.");
    Console.WriteLine();
    Console.Write("Allow remote control of this PC? Type YES to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Remote control was not approved. Linko closed.");
        return;
    }

    using var socket = new ClientWebSocket();
    var wsScheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
    var wsUri = new Uri($"{wsScheme}://{baseUri.Authority}/ws?code={pairingCode}");
    await socket.ConnectAsync(wsUri, CancellationToken.None);

    Console.WriteLine("Connected. Remote control is active.");
    Console.WriteLine("Press Ctrl+C to stop Linko.");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var receiver = ReceiveCommandsAsync(socket, cts.Token);
    var sender = SendFramesAsync(socket, cts.Token);
    await Task.WhenAny(receiver, sender);
    cts.Cancel();

    try { await Task.WhenAll(receiver, sender); } catch { }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"Linko could not start: {ex.Message}");
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
}

static async Task SendFramesAsync(ClientWebSocket socket, CancellationToken token)
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

static async Task ReceiveCommandsAsync(ClientWebSocket socket, CancellationToken token)
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

static void HandleCommand(string text)
{
    try
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (!root.TryGetProperty("type", out var typeElement)) return;
        if (!string.Equals(typeElement.GetString(), "mouse", StringComparison.OrdinalIgnoreCase)) return;

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

static void MouseButton(bool down)
{
    const uint LeftDown = 0x0002;
    const uint LeftUp = 0x0004;
    mouse_event(down ? LeftDown : LeftUp, 0, 0, 0, UIntPtr.Zero);
}

static Bitmap CaptureScreen()
{
    var bounds = SystemInformation.VirtualScreen;
    var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
    return bitmap;
}

static ImageCodecInfo GetJpegEncoder()
{
    return ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
}

[DllImport("user32.dll", SetLastError = true)]
static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
