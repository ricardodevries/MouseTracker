using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MouseTracker
{
    internal partial class Program
    {
        private const string X11Lib = "libX11.so.6";
        private const int Leeway = 5;
        private static readonly Uri WebSocketUri = new("ws://localhost:4455");
        private static ClientWebSocket _webSocket = new();

        [LibraryImport(X11Lib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr XOpenDisplay(string? display);

        [LibraryImport(X11Lib)]
        private static partial int XQueryPointer(IntPtr display, IntPtr window, out IntPtr rootReturn,
            out IntPtr childReturn, out int rootX, out int rootY, out int winX, out int winY, out uint maskReturn);

        [LibraryImport(X11Lib)]
        private static partial IntPtr XRootWindow(IntPtr display, int screenNumber);

        [LibraryImport(X11Lib)]
        private static partial int XDefaultScreen(IntPtr display);

        private static async Task Main()
        {
            IntPtr display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                Console.WriteLine("Unable to open display.");
                return;
            }

            int screenNumber = XDefaultScreen(display);
            IntPtr rootWindow = XRootWindow(display, screenNumber);

            var monitors = new[]
            {
                // xrandr --output eDP1 --mode 1920x1200 --pos 0x0 --rotate normal \
                new
                {
                    Name = "eDP1",
                    X = 0,
                    Y = 0,
                    Width = 1920,
                    Height = 1200,
                },
                // xrandr --output DP3-1 --primary --mode 1920x1080 --pos 1920x0 --rotate normal \
                new
                {
                    Name = "DP3-1",
                    X = 1920,
                    Y = 0,
                    Width = 1920,
                    Height = 1080,
                },
                // xrandr --output DP3-2 --mode 1920x1080 --pos 3840x0 --rotate left
                new
                {
                    Name = "DP3-2",
                    X = 3840,
                    Y = 0,
                    Width = 1080,
                    Height = 1920,
                },
            };

            int prevX = -1;
            int prevY = -1;

            bool leftZoneTriggered = false;
            bool rightZoneTriggered = false;

            _ = Task.Run(ReconnectWebSocketAsync);

            while (true)
            {
                XQueryPointer(display, rootWindow, out _, out _, out int rootX, out int rootY, out _, out _, out _);

                if (Math.Abs(rootX - prevX) <= Leeway && Math.Abs(rootY - prevY) <= Leeway)
                {
                    Thread.Sleep(100);
                    continue;
                }

                prevX = rootX;
                prevY = rootY;

                foreach (var monitor in monitors)
                {
                    if (rootX < monitor.X ||
                        rootX >= monitor.X + monitor.Width ||
                        rootY < monitor.Y ||
                        rootY >= monitor.Y + monitor.Height)
                    {
                        continue;
                    }

                    int relativeX = rootX - monitor.X;
                    int relativeY = rootY - monitor.Y; // I am not using relativeY, but it is here for completeness

                    if (monitor.Name == "DP3-1")
                    {
                        switch (relativeX)
                        {
                            case < 250 when !leftZoneTriggered:
                                leftZoneTriggered = true;
                                rightZoneTriggered = false;
                                await SendWebSocketMessageAsync("Move Camera Right");
                                break;
                            case >= 1650 when !rightZoneTriggered:
                                rightZoneTriggered = true;
                                leftZoneTriggered = false;
                                await SendWebSocketMessageAsync("Move Camera Left");
                                break;
                        }
                    }

                    break;
                }

                Thread.Sleep(100);
            }
        }

        private static async Task ReconnectWebSocketAsync()
        {
            while (true)
            {
                if (_webSocket.State != WebSocketState.Open)
                {
                    _webSocket = new ClientWebSocket();
                    try
                    {
                        await _webSocket.ConnectAsync(WebSocketUri, CancellationToken.None);
                        await HandleWebSocketConnectionAsync();
                        Console.WriteLine("WebSocket connected and authenticated.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"WebSocket connection failed: {ex.Message}");
                        await Task.Delay(5000);
                    }
                }

                await Task.Delay(1000);
            }
        }

        private static async Task HandleWebSocketConnectionAsync()
        {
            try
            {
                byte[] buffer = new byte[1024 * 4];
                var segment = new ArraySegment<byte>(buffer);

                var result = await _webSocket.ReceiveAsync(segment, CancellationToken.None);
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var helloMessage = JsonSerializer.Deserialize<HelloMessage>(message);

                if (helloMessage?.d == null)
                {
                    throw new Exception("Invalid Hello message received.");
                }

                var identifyMessage = new IdentifyMessage
                {
                    op = 1,
                    d = new IdentifyMessage.Data
                    {
                        rpcVersion = helloMessage.d.rpcVersion,
                        eventSubscriptions = 33,
                    },
                };

                string identifyJson = JsonSerializer.Serialize(identifyMessage);
                byte[] identifyBytes = Encoding.UTF8.GetBytes(identifyJson);
                await _webSocket.SendAsync(new ArraySegment<byte>(identifyBytes), WebSocketMessageType.Text, true,
                    CancellationToken.None);

                result = await _webSocket.ReceiveAsync(segment, CancellationToken.None);
                message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var identifiedMessage = JsonSerializer.Deserialize<IdentifiedMessage>(message);

                if (identifiedMessage is not { op: 2 })
                {
                    throw new Exception("Failed to identify with OBS WebSocket.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during WebSocket handling: {ex.Message}");
            }
        }

        private static async Task SendWebSocketMessageAsync(string command)
        {
            try
            {
                var request = new
                {
                    op = 6,
                    d = new
                    {
                        requestType = "TriggerHotkeyByName",
                        requestId = Guid.NewGuid().ToString(),
                        requestData = new
                        {
                            hotkeyName = command,
                        },
                    },
                };

                string requestJson = JsonSerializer.Serialize(request);
                byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);

                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(new ArraySegment<byte>(requestBytes), WebSocketMessageType.Text, true,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during sending WebSocket message: {ex.Message}");
            }
        }
    }

#pragma warning disable CS8602
    public class HelloMessage
    {
        public int op { get; set; }
        public Data d { get; set; }

        public class Data
        {
            public string obsWebSocketVersion { get; set; }
            public int rpcVersion { get; set; }
        }
    }

    public class IdentifiedMessage
    {
        public int op { get; set; }
        public Data d { get; set; }

        public class Data
        {
            public int negotiatedRpcVersion { get; set; }
        }
    }

    public class IdentifyMessage
    {
        public int op { get; set; }
        public Data d { get; set; }

        public class Data
        {
            public int rpcVersion { get; set; }
            public int eventSubscriptions { get; set; }
        }
    }
#pragma warning restore CS8602
}
