using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Anaphora.Core;
using Windows.Graphics.Capture;

namespace Anaphora.CaptureProbe;

/// <summary>
/// The capture half of the probe as an HTTP service, for when the game only runs
/// on another machine. Crop, montage, sample and mask work on PNG files and never
/// needed the game; grabbing a frame is the one step that has to happen where
/// the game is.
///
/// Must be started from the logged-in desktop of the gaming machine -- by
/// double-click or from its own terminal. A process started over SSH or as a
/// service lives in a different session, cannot see the game window, and WGC
/// gets nothing.
///
/// <c>GET /info</c>            window, DPI, display affinity (what probe prints);
///                             <c>?measure=N</c> also counts frames for N seconds
/// <c>GET /frame</c>           one PNG; <c>?x&amp;y&amp;w&amp;h</c> crops at native resolution
///                             before sending (pixels, or fractions if written with a
///                             decimal point), <c>?down=N</c> box-downscales after
/// <c>GET /hud</c>             the production pipeline against the game for
///                             <c>?seconds</c> at <c>?rate</c> Hz; a timeline of reading
///                             changes, rates, and with <c>?verify=N</c> a byte-for-byte
///                             check of every Nth GPU atlas against a CPU copy
/// <c>POST /burst</c>          runs burst here, answers with a tar of the result;
///                             <c>?duration&amp;interval&amp;leadin&amp;full</c> as on the CLI
///
/// Every endpoint takes <c>?process=</c> to target a different window.
/// </summary>
internal static class ServeCommand
{
    public const int DefaultPort = 8750;

    public static int Run(string[] args)
    {
        string processName = args.Length > 0 ? args[0] : "Endfield";
        int port = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : DefaultPort;
        string outputDirectory = Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine("captures", "remote"));

        int session = Process.GetCurrentProcess().SessionId;
        uint consoleSession = Native.WTSGetActiveConsoleSessionId();
        if (session == 0)
        {
            Console.Error.WriteLine("[FAIL] running in session 0 (a service, or a remote shell). WGC cannot see the desktop from here;");
            Console.Error.WriteLine("       start serve from the logged-in desktop of the machine running the game.");
            return 7;
        }

        (string token, string tokenSource) = LoadToken();
        var server = new Server(processName, outputDirectory, token, session, consoleSession);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));

        WebApplication app = builder.Build();
        app.Use(server.Gate);
        app.MapGet("/info", server.Info);
        app.MapGet("/frame", server.Frame);
        app.MapPost("/burst", server.Burst);
        app.MapGet("/hud", server.Hud);

        Console.WriteLine($"serving        : '{processName}' on port {port}");
        Console.WriteLine($"session        : {session} (console session {consoleSession})");
        if (session != consoleSession)
        {
            Console.WriteLine("[warn] this is not the console session. If the game runs on the physical desktop, no window will be found.");
        }

        Console.WriteLine($"burst output   : {outputDirectory}");
        Console.WriteLine($"token          : {token} ({tokenSource})");

        List<(IPAddress Address, string Interface)> addresses = ReachableAddresses();
        foreach ((IPAddress address, string name) in addresses)
        {
            Console.WriteLine($"address        : {address} ({name})");
        }

        Console.WriteLine();
        Console.WriteLine("On the dev machine, put this in remote.local.env at the repo root:");
        Console.WriteLine($"  ANAPHORA_REMOTE=http://{(addresses.Count > 0 ? addresses[0].Address : "<this-machine>")}:{port}");
        Console.WriteLine($"  ANAPHORA_TOKEN={token}");
        Console.WriteLine();
        Console.WriteLine("First run: Windows Firewall will ask about this program; allow it on private networks.");
        Console.WriteLine("Only loopback, private LAN and Tailscale peers are answered. Ctrl+C stops.");
        Console.WriteLine();

        try
        {
            app.Run();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[FAIL] cannot listen on port {port}: {ex.Message}");
            return 8;
        }

        return 0;
    }

    /// <summary>
    /// Persisted, so restarting the server does not mean pasting a new token on
    /// the other machine. The environment variable wins when set.
    /// </summary>
    private static (string Token, string Source) LoadToken()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("ANAPHORA_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return (fromEnvironment.Trim(), "from ANAPHORA_TOKEN");
        }

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anaphora",
            "probe-token.txt");

        if (File.Exists(path))
        {
            string stored = File.ReadAllText(path).Trim();
            if (stored.Length > 0)
            {
                return (stored, $"from {path}");
            }
        }

        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token);
        return (token, $"new, saved to {path}");
    }

    private static List<(IPAddress, string)> ReachableAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => (u.Address, n.Name)))
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                && IsTrustedPeer(a.Address)
                && a.Address.GetAddressBytes()[0] != 169)
            .ToList();

    /// <summary>
    /// Loopback, RFC 1918, link-local, and 100.64.0.0/10 -- carrier-grade NAT
    /// space, which is where Tailscale hands out its addresses. On IPv6, unique
    /// local (Tailscale again) and link-local. The token is the real lock; this
    /// just keeps the port from answering the internet if the machine ever sits
    /// on a public address.
    /// </summary>
    internal static bool IsTrustedPeer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && (b[1] & 0xC0) == 64);
        }

        return address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal;
    }

    private sealed class Server(string processName, string outputDirectory, string token, int session, uint consoleSession)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

        private readonly byte[] expectedAuthorization = Encoding.UTF8.GetBytes($"Bearer {token}");

        /// <summary>
        /// One capture at a time. A burst holds it for its whole run; a single
        /// frame waits briefly for it and then gives up with 409.
        /// </summary>
        private readonly SemaphoreSlim captureGate = new(1, 1);

        public async Task Gate(HttpContext context, RequestDelegate next)
        {
            IPAddress? peer = context.Connection.RemoteIpAddress;
            var clock = Stopwatch.StartNew();

            if (peer is null || !IsTrustedPeer(peer))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
            else if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()),
                expectedAuthorization))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("missing or wrong bearer token\n");
            }
            else
            {
                await next(context);
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[{DateTime.Now:HH:mm:ss}] {peer?.MapToIPv4()} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} -> {context.Response.StatusCode} in {clock.ElapsedMilliseconds} ms"));
        }

        public IResult Info(HttpRequest request)
        {
            string name = ProcessName(request);
            int measure = Math.Clamp(QueryInt(request, "measure") ?? 0, 0, 10);
            GameWindow? window = GameWindow.Find(name);

            object? capture = null;
            if (window is not null && measure > 0 && window.Capturable && !window.Minimised)
            {
                if (!captureGate.Wait(TimeSpan.FromSeconds(5)))
                {
                    return Busy();
                }

                try
                {
                    capture = Measure(window.Handle, measure);
                }
                finally
                {
                    captureGate.Release();
                }
            }

            return Results.Json(
                new
                {
                    server = new
                    {
                        machine = Environment.MachineName,
                        session,
                        consoleSession,
                        burstOutput = outputDirectory,
                        wgcSupported = GraphicsCaptureSession.IsSupported(),
                    },
                    process = name,
                    window = window is null
                        ? null
                        : new
                        {
                            pid = window.ProcessId,
                            hwnd = $"0x{window.Handle:X}",
                            title = window.Title,
                            @class = window.Class,
                            windowRect = Describe(window.WindowRect),
                            clientRect = Describe(window.ClientRect),
                            dpi = window.Dpi,
                            displayAffinity = window.AffinityText,
                            capturable = window.Capturable,
                            minimised = window.Minimised,
                        },
                    capture,
                },
                Json);
        }

        public IResult Frame(HttpRequest request, HttpResponse response)
        {
            string?[] rect = [request.Query["x"], request.Query["y"], request.Query["w"], request.Query["h"]];
            int given = rect.Count(v => !string.IsNullOrEmpty(v));
            if (given is not (0 or 4))
            {
                return Text(StatusCodes.Status400BadRequest, "give all of x, y, w, h, or none of them.");
            }

            int down = QueryInt(request, "down") ?? 1;
            if (down is < 1 or > 16)
            {
                return Text(StatusCodes.Status400BadRequest, "down must be 1..16.");
            }

            if (!captureGate.Wait(TimeSpan.FromSeconds(5)))
            {
                return Busy();
            }

            Frame? frame;
            try
            {
                if (!TryFindWindow(request, out GameWindow? window, out IResult? problem))
                {
                    return problem;
                }

                frame = WgcSession.CaptureOne(window.Handle, FrameTimeout);
            }
            finally
            {
                captureGate.Release();
            }

            if (frame is null)
            {
                return Text(
                    StatusCodes.Status504GatewayTimeout,
                    $"no frame arrived within {FrameTimeout.TotalSeconds:F0}s. The game may have stopped presenting.");
            }

            int x = 0, y = 0, w = frame.Width, h = frame.Height;
            byte[] pixels = frame.Pixels;
            int stride = frame.Stride;

            if (given == 4)
            {
                // Same convention as the crop command: any decimal point means fractions.
                bool normalised = rect.Any(v => v!.Contains('.', StringComparison.Ordinal));
                try
                {
                    x = Math.Clamp(CropCommand.Coord(rect[0]!, frame.Width, normalised), 0, frame.Width - 1);
                    y = Math.Clamp(CropCommand.Coord(rect[1]!, frame.Height, normalised), 0, frame.Height - 1);
                    w = Math.Clamp(CropCommand.Coord(rect[2]!, frame.Width, normalised), 1, frame.Width - x);
                    h = Math.Clamp(CropCommand.Coord(rect[3]!, frame.Height, normalised), 1, frame.Height - y);
                }
                catch (FormatException)
                {
                    return Text(StatusCodes.Status400BadRequest, "x, y, w, h must be numbers.");
                }

                pixels = Png.Crop(frame.Pixels, frame.Stride, x, y, w, h);
                stride = w * 4;
            }

            int outWidth = w, outHeight = h;
            down = Math.Min(down, Math.Min(w, h));
            if (down > 1)
            {
                pixels = Png.Downscale(pixels, w, h, stride, down, out outWidth, out outHeight);
                stride = outWidth * 4;
            }

            using var png = new MemoryStream();
            Png.WriteBgra(png, pixels, outWidth, outHeight, stride);

            response.Headers["X-Anaphora-Source"] = $"{frame.Width}x{frame.Height}";
            response.Headers["X-Anaphora-Rect"] = $"{x},{y},{w},{h}";
            response.Headers["X-Anaphora-Normalised"] = string.Create(
                CultureInfo.InvariantCulture,
                $"{(double)x / frame.Width:F4},{(double)y / frame.Height:F4},{(double)w / frame.Width:F4},{(double)h / frame.Height:F4}");
            return Results.Bytes(png.ToArray(), "image/png");
        }

        public IResult Burst(HttpRequest request)
        {
            int duration = QueryInt(request, "duration") ?? 60;
            int interval = QueryInt(request, "interval") ?? 1000;
            int leadIn = QueryInt(request, "leadin") ?? 0;
            int fullEvery = QueryInt(request, "full") ?? 4;

            if (duration is < 1 or > 600 || interval is < 50 or > 60_000 || leadIn is < 0 or > 300 || fullEvery < 1)
            {
                return Text(
                    StatusCodes.Status400BadRequest,
                    "duration 1..600 s, interval 50..60000 ms, leadin 0..300 s, full >= 1.");
            }

            if (!captureGate.Wait(0))
            {
                return Busy();
            }

            string directory;
            try
            {
                if (!TryFindWindow(request, out GameWindow? window, out IResult? problem))
                {
                    return problem;
                }

                directory = Path.Combine(outputDirectory, $"burst-{DateTime.Now:yyyyMMdd-HHmmss}");
                Console.WriteLine();
                int code = BurstCommand.Run(window.Handle, directory, duration, interval, leadIn, fullEvery);
                Console.WriteLine();
                if (code != 0)
                {
                    return Text(StatusCodes.Status500InternalServerError, $"burst failed (exit {code}); the server console has the details.");
                }
            }
            finally
            {
                captureGate.Release();
            }

            // The frames stay on this machine too, so a dropped transfer loses nothing.
            return Results.Stream(
                body => TarFile.CreateFromDirectoryAsync(directory, body, includeBaseDirectory: false),
                "application/x-tar",
                $"{Path.GetFileName(directory)}.tar");
        }

        /// <summary>
        /// The production pipeline against the live game: what it read, as a
        /// timeline of changes, plus rates and verification counts.
        /// </summary>
        public IResult Hud(HttpRequest request)
        {
            int seconds = QueryInt(request, "seconds") ?? 10;
            int rate = QueryInt(request, "rate") ?? 20;
            int verify = QueryInt(request, "verify") ?? 0;

            if (seconds is < 1 or > 300 || rate is < 1 or > 120 || verify < 0)
            {
                return Text(StatusCodes.Status400BadRequest, "seconds 1..300, rate 1..120, verify >= 0.");
            }

            GameProfile profile;
            try
            {
                profile = HudCommand.LoadProfile(null);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return Text(StatusCodes.Status500InternalServerError, $"cannot load the profile: {ex.Message}");
            }

            if (!captureGate.Wait(0))
            {
                return Busy();
            }

            HudRun run;
            try
            {
                if (!TryFindWindow(request, out GameWindow? window, out IResult? problem))
                {
                    return problem;
                }

                run = HudCommand.Capture(window.Handle, profile, TimeSpan.FromSeconds(seconds), rate, verify, log: null);
            }
            finally
            {
                captureGate.Release();
            }

            Console.WriteLine(run.Summary());
            return Results.Json(
                new
                {
                    profile = profile.Id,
                    seconds = Math.Round(run.Seconds, 2),
                    run.Stats,
                    run.Snapshots,
                    hudPresent = run.Present,
                    run.Closed,
                    failure = run.Failure?.ToString(),
                    timeline = run.Timeline,
                },
                Json);
        }

        private string ProcessName(HttpRequest request)
        {
            string? requested = request.Query["process"];
            return string.IsNullOrWhiteSpace(requested) ? processName : requested;
        }

        private bool TryFindWindow(
            HttpRequest request,
            [NotNullWhen(true)] out GameWindow? window,
            [NotNullWhen(false)] out IResult? problem)
        {
            string name = ProcessName(request);
            window = GameWindow.Find(name);
            problem = window switch
            {
                null => Text(StatusCodes.Status404NotFound, $"no process named '{name}' with a top-level window."),
                { Capturable: false } => Text(StatusCodes.Status409Conflict, $"the window opts out of capture: {window.AffinityText}."),
                { Minimised: true } => Text(StatusCodes.Status409Conflict, "the window is minimised; WGC has nothing to capture until it is restored."),
                _ => null,
            };

            return problem is null;
        }

        private static object Measure(IntPtr hwnd, int seconds)
        {
            using WgcSession capture = WgcSession.Create(hwnd, suppressBorder: true);
            int frames = 0;
            capture.Start(pool =>
            {
                using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
                if (frame is not null)
                {
                    Interlocked.Increment(ref frames);
                }
            });

            var clock = Stopwatch.StartNew();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            double elapsed = clock.Elapsed.TotalSeconds;
            int counted = Volatile.Read(ref frames);

            return new
            {
                item = $"{capture.Item.Size.Width}x{capture.Item.Size.Height}",
                frames = counted,
                seconds = Math.Round(elapsed, 2),
                fps = Math.Round(counted / elapsed, 1),
            };
        }

        private static object? Describe(Native.Rect? rect) => rect is Native.Rect r
            ? new { left = r.Left, top = r.Top, width = r.Width, height = r.Height }
            : null;

        private static int? QueryInt(HttpRequest request, string name) =>
            int.TryParse(request.Query[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

        private static IResult Text(int status, string message) => Results.Text(message + "\n", statusCode: status);

        private static IResult Busy() =>
            Text(StatusCodes.Status409Conflict, "busy: another capture (probably a burst) is running.");
    }
}
