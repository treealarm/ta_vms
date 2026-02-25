using System.Net;
using System.Net.Sockets;
using Grpc.Net.Client;
using Vms.Control;

var startup = ParseStartupArguments(args);
if (!startup.Success)
{
    Console.Error.WriteLine(startup.ErrorMessage);
    PrintUsage();
    return 1;
}

if (startup.ShowHelp)
{
    PrintUsage();
    return 0;
}

var endpoint = EnsureHttpScheme(startup.Address);
using var channel = GrpcChannel.ForAddress(endpoint);
var client = new CameraControlService.CameraControlServiceClient(channel);
await using var jpegReceiver = new JpegReceiverManager();

if (startup.SingleRunCommand is not null)
{
    return await ExecuteCommandLineAsync(client, jpegReceiver, startup.Address, startup.SingleRunCommand);
}

Console.WriteLine($"Connected to {endpoint}");
Console.WriteLine("Interactive mode. Type 'help' for commands, 'exit' to quit.");

while (true)
{
    Console.Write("vms> ");
    var line = Console.ReadLine();

    if (line is null)
    {
        break;
    }

    line = line.Trim();
    if (line.Length == 0)
    {
        continue;
    }

    if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (line.Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        PrintInteractiveHelp();
        continue;
    }

    var commandArgs = SplitCommandLine(line);
    if (commandArgs.Count == 0)
    {
        continue;
    }

    var parsed = ParseCommandArguments(startup.Address, commandArgs.ToArray());
    if (!parsed.Success)
    {
        Console.Error.WriteLine(parsed.ErrorMessage);
        continue;
    }

    var code = await ExecuteParsedCommandAsync(client, jpegReceiver, parsed);
    if (code != 0)
    {
        Console.Error.WriteLine($"Command finished with code {code}.");
    }
}

return 0;

static async Task<int> ExecuteCommandLineAsync(
    CameraControlService.CameraControlServiceClient client,
    JpegReceiverManager receiver,
    string address,
    string[] commandArgs)
{
    var parsed = ParseCommandArguments(address, commandArgs);
    if (!parsed.Success)
    {
        Console.Error.WriteLine(parsed.ErrorMessage);
        return 1;
    }

    return await ExecuteParsedCommandAsync(client, receiver, parsed);
}

static async Task<int> ExecuteParsedCommandAsync(
    CameraControlService.CameraControlServiceClient client,
    JpegReceiverManager receiver,
    ParsedArguments parsed)
{
    try
    {
        if (parsed.Command == "start-jpeg" && parsed.Port.HasValue)
        {
            receiver.EnsureListener(parsed.Port.Value);
        }

        if (parsed.Command == "stop-jpeg")
        {
            receiver.StopListener();
        }

        var response = await SendCommandAsync(client, parsed);
        Console.WriteLine($"success={response.Success} message=\"{response.Message}\"");
        return response.Success ? 0 : 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"command failed: {ex.Message}");
        return 3;
    }
}

static async Task<OperationStatus> SendCommandAsync(
    CameraControlService.CameraControlServiceClient client,
    ParsedArguments parsed)
{
    return parsed.Command switch
    {
        "add" => await client.AddCameraAsync(new AddCameraRequest
        {
            CameraId = parsed.CameraId,
            RtspUrl = parsed.RtspUrl ?? string.Empty,
            StartImmediately = parsed.StartImmediately
        }),
        "start" => await client.StartRecordingAsync(new CameraRequest { CameraId = parsed.CameraId }),
        "stop" => await client.StopRecordingAsync(new CameraRequest { CameraId = parsed.CameraId }),
        "start-jpeg" => await client.StartJpegStreamAsync(new JpegStreamRequest
        {
            CameraId = parsed.CameraId,
            Host = parsed.Host ?? string.Empty,
            Port = (uint)(parsed.Port ?? 0)
        }),
        "stop-jpeg" => await client.StopJpegStreamAsync(new CameraRequest { CameraId = parsed.CameraId }),
        _ => throw new InvalidOperationException($"Unknown command '{parsed.Command}'.")
    };
}

static StartupArguments ParseStartupArguments(string[] args)
{
    if (args.Any(a => a is "--help" or "-h"))
    {
        return StartupArguments.Help();
    }

    var address = "localhost:50051";
    var command = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        var token = args[i];
        if (token is "--address" or "-a")
        {
            if (i + 1 >= args.Length)
            {
                return StartupArguments.Fail("Missing value for --address.");
            }

            address = args[++i];
            continue;
        }

        command.Add(token);
    }

    return StartupArguments.Ok(address, command.Count == 0 ? null : command.ToArray());
}

static ParsedArguments ParseCommandArguments(string address, string[] args)
{
    if (args.Length == 0)
    {
        return ParsedArguments.Fail("Missing command.");
    }

    var command = args[0].ToLowerInvariant();

    return command switch
    {
        "add" => ParseAdd(address, args),
        "start" => ParseSimpleCameraCommand(address, command, args),
        "stop" => ParseSimpleCameraCommand(address, command, args),
        "start-jpeg" => ParseStartJpeg(address, args),
        "stop-jpeg" => ParseSimpleCameraCommand(address, command, args),
        _ => ParsedArguments.Fail($"Unknown command '{args[0]}'.")
    };
}

static ParsedArguments ParseAdd(string address, string[] tokens)
{
    if (tokens.Length < 3)
    {
        return ParsedArguments.Fail("Command 'add' expects: add <camera-id> <rtsp-url> [--start].");
    }

    var cameraId = tokens[1];
    var rtspUrl = tokens[2];
    var startImmediately = tokens.Skip(3).Any(t => t is "--start" or "-s");

    var unexpected = tokens.Skip(3).Where(t => t is not "--start" and not "-s").ToList();
    if (unexpected.Count > 0)
    {
        return ParsedArguments.Fail($"Unknown option(s) for add: {string.Join(", ", unexpected)}");
    }

    return ParsedArguments.Ok(address, "add", cameraId, rtspUrl, startImmediately);
}

static ParsedArguments ParseStartJpeg(string address, string[] tokens)
{
    if (tokens.Length != 4)
    {
        return ParsedArguments.Fail("Command 'start-jpeg' expects: start-jpeg <camera-id> <host> <port>.");
    }

    if (!int.TryParse(tokens[3], out var port) || port <= 0 || port > 65535)
    {
        return ParsedArguments.Fail("Port must be an integer in range 1..65535.");
    }

    return ParsedArguments.Ok(address, "start-jpeg", tokens[1], null, false, tokens[2], port);
}

static ParsedArguments ParseSimpleCameraCommand(string address, string command, string[] tokens)
{
    if (tokens.Length != 2)
    {
        return ParsedArguments.Fail($"Command '{command}' expects: {command} <camera-id>.");
    }

    return ParsedArguments.Ok(address, command, tokens[1], null, false);
}

static List<string> SplitCommandLine(string input)
{
    var result = new List<string>();
    var current = new List<char>();
    var inQuotes = false;

    foreach (var ch in input)
    {
        if (ch == '"')
        {
            inQuotes = !inQuotes;
            continue;
        }

        if (char.IsWhiteSpace(ch) && !inQuotes)
        {
            if (current.Count > 0)
            {
                result.Add(new string(current.ToArray()));
                current.Clear();
            }

            continue;
        }

        current.Add(ch);
    }

    if (current.Count > 0)
    {
        result.Add(new string(current.ToArray()));
    }

    return result;
}

static string EnsureHttpScheme(string address) =>
    address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
    address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? address
        : $"http://{address}";

static void PrintUsage()
{
    Console.WriteLine("VMS Control test client (gRPC, .NET 8)");
    Console.WriteLine();
    Console.WriteLine("Non-interactive usage:");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] add <camera-id> <rtsp-url> [--start]");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] start <camera-id>");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] stop <camera-id>");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] start-jpeg <camera-id> <host> <port>");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] stop-jpeg <camera-id>");
    Console.WriteLine();
    Console.WriteLine("Interactive mode:");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port]");
}

static void PrintInteractiveHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  add <camera-id> <rtsp-url> [--start]");
    Console.WriteLine("  start <camera-id>");
    Console.WriteLine("  stop <camera-id>");
    Console.WriteLine("  start-jpeg <camera-id> <host> <port>");
    Console.WriteLine("  stop-jpeg <camera-id>");
    Console.WriteLine("  help");
    Console.WriteLine("  exit");
}

internal sealed record StartupArguments(
    bool Success,
    bool ShowHelp,
    string Address,
    string[]? SingleRunCommand,
    string? ErrorMessage)
{
    public static StartupArguments Ok(string address, string[]? command) =>
        new(true, false, address, command, null);

    public static StartupArguments Help() =>
        new(true, true, "localhost:50051", null, null);

    public static StartupArguments Fail(string error) =>
        new(false, false, string.Empty, null, error);
}

internal sealed record ParsedArguments(
    bool Success,
    string? ErrorMessage,
    string Address,
    string Command,
    string CameraId,
    string? RtspUrl,
    bool StartImmediately,
    string? Host,
    int? Port)
{
    public static ParsedArguments Ok(string address, string command, string cameraId, string? rtspUrl, bool startImmediately, string? host = null, int? port = null) =>
        new(true, null, address, command, cameraId, rtspUrl, startImmediately, host, port);

    public static ParsedArguments Fail(string errorMessage) =>
        new(false, errorMessage, string.Empty, string.Empty, string.Empty, null, false, null, null);
}

internal sealed class JpegReceiverManager : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _frameCounter;

    public void EnsureListener(int port)
    {
        if (_listener is not null)
        {
            var activePort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            if (activePort == port)
            {
                return;
            }

            StopListener();
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        Console.WriteLine($"JPEG receiver listening on 0.0.0.0:{port}");
    }

    public void StopListener()
    {
        if (_listener is null)
        {
            return;
        }

        _cts?.Cancel();
        _listener.Stop();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _acceptTask = null;
        Console.WriteLine("JPEG receiver stopped.");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "jpeg_frames");
        Directory.CreateDirectory(outputDir);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = await listener.AcceptSocketAsync(ct);
                Console.WriteLine($"JPEG sender connected: {socket.RemoteEndPoint}");
                using var stream = new NetworkStream(socket, ownsSocket: false);
                await ReadFramesAsync(stream, outputDir, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"JPEG receiver error: {ex.Message}");
            }
        }
    }

    private async Task ReadFramesAsync(Stream stream, string outputDir, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var lenBuffer = new byte[4];
            var read = await ReadExactAsync(stream, lenBuffer, ct);
            if (!read)
            {
                return;
            }

            var frameSize = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuffer, 0));
            if (frameSize <= 0 || frameSize > 10 * 1024 * 1024)
            {
                Console.Error.WriteLine($"Invalid frame size: {frameSize}");
                return;
            }

            var frameBuffer = new byte[frameSize];
            if (!await ReadExactAsync(stream, frameBuffer, ct))
            {
                return;
            }

            var frameNo = Interlocked.Increment(ref _frameCounter);
            var filePath = Path.Combine(outputDir, $"frame_{frameNo:000000}.jpg");
            await File.WriteAllBytesAsync(filePath, frameBuffer, ct);

            if (frameNo % 25 == 0)
            {
                Console.WriteLine($"Received {frameNo} JPEG frames. Last: {filePath}");
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        StopListener();
        return ValueTask.CompletedTask;
    }
}
