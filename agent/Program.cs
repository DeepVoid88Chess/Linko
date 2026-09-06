using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

const long JpegQuality = 55L;
const int FrameIntervalMs = 100;
const string LinkoServerUrl = "https://linko.jacksonbickleythomas.workers.dev";

Console.Title = "Linko";
Console.WriteLine("Linko");
Console.WriteLine("Your computer. Wherever you are.");
Console.WriteLine();
Console.WriteLine("Connecting to Linko...");

string pairingCode;
Uri baseUri;

try
{
    baseUri = new Uri(LinkoServerUrl);
    using var http = new HttpClient();
    using var response = await http.PostAsync(new Uri(baseUri, "/api/pair"), null);
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    pairingCode = json.RootElement.GetProperty("pairingCode").GetString() ?? throw new Exception("No pairing code returned.");
}
catch (Exception ex)
{
    Console.WriteLine($"Could not connect: {ex.Message}");
    Console.WriteLine("Press Enter to exit.");
    Console.ReadLine();
    return;
}

Console.Clear();
Console.WriteLine("Linko");
Console.WriteLine("================================");
Console.WriteLine();
Console.WriteLine($"PAIRING CODE: {pairingCode.Insert(3, " ")}");
Console.WriteLine();
Console.WriteLine("Enter this code on your iPad.");
Console.WriteLine();
Console.Write("Allow remote control? Type YES to approve: ");
var approval = Console.ReadLine();

if (!string.Equals(approval?.Trim(), "YES", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Remote control was not approved.");
    return;
}

using var socket = new ClientWebSocket();
using var cts = new CancellationTokenSource();

try
{
    var wsScheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
    var wsUri = new Uri($"{wsScheme}://{baseUri.Authority}/ws?code={pairingCode}");
    Console.WriteLine("Connecting to your iPad...");
    await socket.ConnectAsync(wsUri, cts.Token);
    Console.WriteLine("Connected. Remote control is active.");
    Console.WriteLine("Press Ctrl+C to stop Linko.");

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var receiver = ReceiveCommandsAsync(socket, cts.Token);
    var sender = SendFramesAsync(socket, cts.Token);
    await Task.WhenAny(receiver, sender);
}
catch (OperationCanceledException)
{
}
catch (Exception ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
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
        catch (OperationCanceledException)
        {
            break;
        }
        catch
        {
            try { await Task.Delay(500, token); } catch { break; }
        }
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
        catch (OperationCanceledException)
        {
            break;
        }
        catch
        {
        }
    }
}

static void HandleCommand(string text)
{
    try
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        if (!root.TryGetProperty("type", out var typeElement) ||
            !string.Equals(typeElement.GetString(), "mouse", StringComparison.OrdinalIgnoreCase))
            return;

        var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
        var x = root.TryGetProperty("x", out var xElement) ? xElement.GetDouble() : 0.5;
        var y = root.TryGetProperty("y", out var yElement) ? yElement.GetDouble() : 0.5;

        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);

        var bounds = GetVirtualScreen();
        SetCursorPos(
            bounds.Left + (int)Math.Round(x * Math.Max(0, bounds.Width - 1)),
            bounds.Top + (int)Math.Round(y * Math.Max(0, bounds.Height - 1)));

        if (action == "down") MouseButton(true);
        else if (action == "up") MouseButton(false);
    }
    catch
    {
    }
}

static void MouseButton(bool down)
{
    const uint LeftDown = 0x0002;
    const uint LeftUp = 0x0004;
    mouse_event(down ? LeftDown : LeftUp, 0, 0, 0, UIntPtr.Zero);
}

static Bitmap CaptureScreen()
{
    var bounds = GetVirtualScreen();
    var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);

    using var graphics = Graphics.FromImage(bitmap);
    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
    return bitmap;
}

static Rectangle GetVirtualScreen()
{
    return new Rectangle(
        GetSystemMetrics(76),
        GetSystemMetrics(77),
        GetSystemMetrics(78),
        GetSystemMetrics(79));
}

static ImageCodecInfo GetJpegEncoder() =>
    ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

[DllImport("user32.dll", SetLastError = true)]
static extern bool SetCursorPos(int x, int y);

[DllImport("user32.dll", SetLastError = true)]
static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

[DllImport("user32.dll")]
static extern int GetSystemMetrics(int nIndex);
