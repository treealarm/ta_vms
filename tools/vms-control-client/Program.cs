using Grpc.Core;
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
await using var jpegReceiver = new JpegReceiverManager(client);

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

    var commandArgs = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var parsed = ParseCommandArguments(startup.Address, commandArgs);
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
        if (parsed.Command == "start-jpeg")
        {
            var response = await client.StartJpegStreamAsync(new JpegStreamRequest
            {
                CameraId = parsed.CameraId
            });

            Console.WriteLine($"success={response.Success} message=\"{response.Message}\"");
            if (!response.Success)
            {
                return 2;
            }

            receiver.Start(parsed.CameraId);
            return 0;
        }

        if (parsed.Command == "stop-jpeg")
        {
            receiver.Stop();
        }

        var response2 = await SendCommandAsync(client, parsed);
        Console.WriteLine($"success={response2.Success} message=\"{response2.Message}\"");
        return response2.Success ? 0 : 2;
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
        "start-jpeg" => ParseSimpleCameraCommand(address, command, args),
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
        return ParsedArguments.Fail($"Unexpected arguments for 'add': {string.Join(' ', unexpected)}");
    }

    return ParsedArguments.Ok(address, "add", cameraId, rtspUrl, startImmediately);
}

static ParsedArguments ParseSimpleCameraCommand(string address, string command, string[] tokens)
{
    if (tokens.Length != 2)
    {
        return ParsedArguments.Fail($"Command '{command}' expects: {command} <camera-id>.");
    }

    return ParsedArguments.Ok(address, command, tokens[1], null, false);
}

static string EnsureHttpScheme(string address)
{
    if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return address;
    }

    return $"http://{address}";
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] add <camera-id> <rtsp-url> [--start]");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] start <camera-id>");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] stop <camera-id>");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] start-jpeg <camera-id>");
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
    Console.WriteLine("  start-jpeg <camera-id>");
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
    bool StartImmediately)
{
    public static ParsedArguments Ok(string address, string command, string cameraId, string? rtspUrl, bool startImmediately) =>
        new(true, null, address, command, cameraId, rtspUrl, startImmediately);

    public static ParsedArguments Fail(string errorMessage) =>
        new(false, errorMessage, string.Empty, string.Empty, string.Empty, null, false);
}

internal sealed class JpegReceiverManager(CameraControlService.CameraControlServiceClient client) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _frameCounter;

    public void Start(string cameraId)
    {
        Stop();

        _cts = new CancellationTokenSource();
        _task = Task.Run(() => ReadFramesAsync(cameraId, _cts.Token));
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _task = null;
        Console.WriteLine("JPEG receiver stopped.");
    }

    private async Task ReadFramesAsync(string cameraId, CancellationToken ct)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "jpeg_frames");
        Directory.CreateDirectory(outputDir);

        try
        {
            using var call = client.StreamJpegFrames(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);
            Console.WriteLine($"Receiving JPEG frames for camera '{cameraId}' via gRPC stream.");

            await foreach (var frame in call.ResponseStream.ReadAllAsync(ct))
            {
                var frameNo = Interlocked.Increment(ref _frameCounter);
                var filePath = Path.Combine(outputDir, $"frame_{frameNo:000000}.jpg");
                await File.WriteAllBytesAsync(filePath, frame.JpegData.ToByteArray(), ct);

                if (frameNo % 25 == 0)
                {
                    //Console.WriteLine($"Received {frameNo} JPEG frames. Last: {filePath}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"JPEG stream error: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
