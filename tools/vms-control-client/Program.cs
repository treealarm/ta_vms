using Grpc.Net.Client;
using Vms.Control;

if (args.Length == 0 || HasHelpFlag(args))
{
    PrintUsage();
    return 1;
}

var parsed = ParseArguments(args);
if (!parsed.Success)
{
    Console.Error.WriteLine(parsed.ErrorMessage);
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

var endpoint = EnsureHttpScheme(parsed.Address);

using var channel = GrpcChannel.ForAddress(endpoint);
var client = new CameraControlService.CameraControlServiceClient(channel);

try
{
    var response = await SendCommandAsync(client, parsed.Command, parsed.CameraId, parsed.RtspUrl, parsed.StartImmediately);
    Console.WriteLine($"success={response.Success} message=\"{response.Message}\"");
    return response.Success ? 0 : 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"gRPC request failed: {ex.Message}");
    return 3;
}

static bool HasHelpFlag(string[] args) => args.Any(a => a is "--help" or "-h");

static string EnsureHttpScheme(string address) =>
    address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
    address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? address
        : $"http://{address}";

static async Task<OperationStatus> SendCommandAsync(
    CameraControlService.CameraControlServiceClient client,
    string command,
    string cameraId,
    string? rtspUrl,
    bool startImmediately)
{
    return command switch
    {
        "add" => await client.AddCameraAsync(new AddCameraRequest
        {
            CameraId = cameraId,
            RtspUrl = rtspUrl ?? string.Empty,
            StartImmediately = startImmediately
        }),
        "start" => await client.StartRecordingAsync(new CameraRequest { CameraId = cameraId }),
        "stop" => await client.StopRecordingAsync(new CameraRequest { CameraId = cameraId }),
        _ => throw new InvalidOperationException($"Unknown command '{command}'.")
    };
}

static ParsedArguments ParseArguments(string[] args)
{
    var address = "localhost:50051";
    var filtered = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        var token = args[i];
        if (token is "--address" or "-a")
        {
            if (i + 1 >= args.Length)
            {
                return ParsedArguments.Fail("Missing value for --address.");
            }

            address = args[++i];
            continue;
        }

        filtered.Add(token);
    }

    if (filtered.Count == 0)
    {
        return ParsedArguments.Fail("Missing command.");
    }

    var command = filtered[0].ToLowerInvariant();

    return command switch
    {
        "add" => ParseAdd(address, filtered),
        "start" => ParseSimpleCameraCommand(address, command, filtered),
        "stop" => ParseSimpleCameraCommand(address, command, filtered),
        _ => ParsedArguments.Fail($"Unknown command '{filtered[0]}'.")
    };
}

static ParsedArguments ParseAdd(string address, List<string> tokens)
{
    if (tokens.Count < 3)
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

static ParsedArguments ParseSimpleCameraCommand(string address, string command, List<string> tokens)
{
    if (tokens.Count != 2)
    {
        return ParsedArguments.Fail($"Command '{command}' expects: {command} <camera-id>.");
    }

    return ParsedArguments.Ok(address, command, tokens[1], null, false);
}

static void PrintUsage()
{
    Console.WriteLine("VMS Control test client (gRPC, .NET 8)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] add <camera-id> <rtsp-url> [--start]");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] start <camera-id>");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- [--address host:port] stop <camera-id>");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- add cam-1 rtsp://127.0.0.1:8554/live --start");
    Console.WriteLine("  dotnet run --project tools/vms-control-client -- --address localhost:50051 stop cam-1");
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
