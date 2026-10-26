using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Cfg;

namespace VmsCfg;

/// <summary>
/// The deployment's nodes (media_server_node) and whether a machine may join without an
/// administrator's password (deployment).
/// </summary>
/// <remarks>
/// Read from the database on every call, never cached: VmsCfg may run in several replicas, and a
/// copy in one of them would miss a node another enrolled. The table holds a handful of rows.
/// </remarks>
public sealed class NodeRegistry(
  IDbContextFactory<VmsCfgDbContext> dbFactory,
  CameraStorage storage,
  ILogger<NodeRegistry> logger)
{
  // Set on the core of a deployment over several machines, where ../ta_install keeps its keys
  // (TaVmsHost core, scripts/distributed.sh core). Optional: on a single machine it is absent, and
  // there is nothing to enroll into.
  private static readonly string? DistributedDir =
    Environment.GetEnvironmentVariable("TAVMS_DISTRIBUTED_DIR") is { Length: > 0 } dir ? dir : null;

  private static readonly string? CoreAddress =
    Environment.GetEnvironmentVariable("CORE_ADDRESS") is { Length: > 0 } a ? a : null;

  /// <summary>
  /// The nodes this machine's MEDIA_SERVER_PAIRS names: the media server, the VmsFs that owns its
  /// archive volume, the VmsOnvif that shares its camera subnet, the VmsLogger that can read its
  /// log files, and optionally the roitrc that can reach the same disk.
  /// </summary>
  /// <remarks>
  /// All of them are pinned to the same machine, so the entry is the node — there is no arrangement
  /// in which they can be picked independently. Parsed at startup so that a malformed value stops
  /// the process while someone is still watching it.
  /// </remarks>
  public static IReadOnlyList<(string ServerId, string FsId, string OnvifId, string LoggerId, string RoiId)> ConfiguredPairs()
  {
    var raw = Environment.GetEnvironmentVariable("MEDIA_SERVER_PAIRS")
      ?? throw new InvalidOperationException("Required environment variable 'MEDIA_SERVER_PAIRS' is not set");

    var pairs = new List<(string, string, string, string, string)>();
    foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      // The fifth part is the ROI transcoder and is optional — a node that does not re-compress
      // simply leaves it off. The first four are not: an entry missing one of those leaves
      // web_srv.Server with no instance to send PTZ to, or none that can read that node's logs,
      // and there is nothing sensible to guess.
      var parts = entry.Split(':', StringSplitOptions.TrimEntries);
      if (parts.Length is not (4 or 5) || parts.Take(4).Any(string.IsNullOrEmpty))
        throw new InvalidOperationException(
          $"Invalid MEDIA_SERVER_PAIRS entry '{entry}': expected "
          + "'server_id:fs_id:onvif_id:logger_id' with an optional ':roi_id'");
      pairs.Add((parts[0], parts[1], parts[2], parts[3], parts.Length == 5 ? parts[4] : string.Empty));
    }

    if (pairs.Count == 0)
      throw new InvalidOperationException(
        "MEDIA_SERVER_PAIRS must contain at least one 'server_id:fs_id:onvif_id:logger_id' entry");

    return pairs;
  }

  /// <summary>
  /// Writes MEDIA_SERVER_PAIRS into the table, at every start: its rows are marked configured, and a
  /// row the variable no longer names stops being configured, so that it can be removed.
  /// </summary>
  /// <remarks>Raw SQL for the upsert, which EF cannot express.</remarks>
  public static async Task SeedConfiguredAsync(NpgsqlConnection connection, CancellationToken ct)
  {
    var pairs = ConfiguredPairs();
    foreach (var (serverId, fsId, onvifId, loggerId, roiId) in pairs)
    {
      await using var upsert = new NpgsqlCommand("""
        INSERT INTO media_server_node (server_id, fs_id, onvif_id, logger_id, roi_id, name, configured)
        VALUES ($1, $2, $3, $4, $5, $1, true)
        ON CONFLICT (server_id) DO UPDATE SET
          fs_id = EXCLUDED.fs_id, onvif_id = EXCLUDED.onvif_id, logger_id = EXCLUDED.logger_id,
          roi_id = EXCLUDED.roi_id, configured = true;
        """, connection);
      upsert.Parameters.AddWithValue(serverId);
      upsert.Parameters.AddWithValue(fsId);
      upsert.Parameters.AddWithValue(onvifId);
      upsert.Parameters.AddWithValue(loggerId);
      upsert.Parameters.AddWithValue(roiId);
      await upsert.ExecuteNonQueryAsync(ct);
    }

    await using var release = new NpgsqlCommand(
      "UPDATE media_server_node SET configured = false WHERE configured AND server_id <> ALL($1);", connection);
    release.Parameters.AddWithValue(pairs.Select(p => p.ServerId).ToArray());
    await release.ExecuteNonQueryAsync(ct);
  }

  public async Task<List<DbMediaServerNode>> ListAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.MediaServerNodes.AsNoTracking()
      .OrderByDescending(n => n.Configured).ThenBy(n => n.Name)
      .ToListAsync(ct);
  }

  public async Task<bool> IsKnownServerAsync(string serverId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.MediaServerNodes.AnyAsync(n => n.ServerId == serverId, ct);
  }

  public async Task<Deployment> GetDeploymentAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var open = await db.Deployments.Where(d => d.Id == 1).Select(d => d.EnrollmentOpen).SingleAsync(ct);
    return new Deployment
    {
      Distributed = DistributedDir is not null,
      CoreAddress = CoreAddress ?? string.Empty,
      EnrollmentOpen = open,
    };
  }

  public async Task SetEnrollmentOpenAsync(bool open, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await db.Deployments.Where(d => d.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(d => d.EnrollmentOpen, open), ct);
    logger.LogInformation("Node enrollment {State}", open ? "opened: machines join without a password" : "closed: joining takes an administrator's password");
  }

  /// <summary>
  /// Registers a node and makes its package. Asked again for the same name from the same address —
  /// a machine installed over — it hands out a new package for the node already there.
  /// </summary>
  public async Task<(DbMediaServerNode Node, byte[] Package)> EnrollAsync(
    string name, string address, bool mediaMtx, CancellationToken ct)
  {
    if (DistributedDir is null)
      throw new RpcException(new Status(StatusCode.FailedPrecondition,
        "this is not the core of a deployment over several machines (install it with Role=core)"));
    if (NodePackage.NameProblem(name) is { } nameProblem)
      throw new RpcException(new Status(StatusCode.InvalidArgument, nameProblem));
    if (NodePackage.AddressProblem(address) is { } addressProblem)
      throw new RpcException(new Status(StatusCode.InvalidArgument, addressProblem));

    var (serverId, fsId, onvifId, loggerId) = NodePackage.Pair(name);
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var existing = await db.MediaServerNodes.SingleOrDefaultAsync(n => n.ServerId == serverId || n.Name == name, ct);
    if (existing is not null && (existing.Configured || existing.Address != address))
      throw new RpcException(new Status(StatusCode.AlreadyExists, existing.Configured
        ? $"node '{name}' is listed in this core's configuration; it joined with a package before"
        : $"there is a node '{name}' at {existing.Address} already; remove it first, or choose another name"));

    // Made before anything is written: a core that cannot sign must not leave a node behind that
    // has no package.
    var package = NodePackage.Build(name,
      await File.ReadAllTextAsync(Path.Combine(DistributedDir, "core", "jwt.key"), ct),
      await File.ReadAllTextAsync(Path.Combine(DistributedDir, "shared.env"), ct),
      Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal),
      DateTimeOffset.UtcNow);

    var node = existing ?? db.MediaServerNodes.Add(new DbMediaServerNode
    {
      ServerId = serverId, FsId = fsId, OnvifId = onvifId, LoggerId = loggerId, Name = name,
      Address = address, EnrolledAt = DateTime.UtcNow,
    }).Entity;
    node.MediaMtx = mediaMtx;
    await db.SaveChangesAsync(ct);

    if (mediaMtx)
      await EnsureMediaMtxAsync(node, ct);
    logger.LogInformation("Node '{Name}' ({ServerId}) at {Address} {How}", name, serverId, address,
      existing is null ? "enrolled" : "enrolled again: a new package for it");
    return (node, package);
  }

  /// <summary>
  /// The node's own MediaMTX, so that its cameras' live video stays on it: the accounts of the
  /// shared one, every MediaMTX of a deployment using the core's, at the node's address.
  /// </summary>
  private async Task EnsureMediaMtxAsync(DbMediaServerNode node, CancellationToken ct)
  {
    var servers = await storage.ListMediaMtxServersAsync(ct);
    if (servers.Any(s => s.ServerId == node.ServerId))
      return;
    var shared = servers.FirstOrDefault(s => string.IsNullOrEmpty(s.ServerId));
    if (shared is null)
    {
      logger.LogWarning("No shared MediaMTX to take accounts from: add node '{Name}''s in the web interface", node.Name);
      return;
    }
    await storage.UpsertMediaMtxServerAsync(new MediaMtxServer
    {
      Name = node.Name, Host = node.Address, ServerId = node.ServerId,
      RtspPort = shared.RtspPort, WebrtcPort = shared.WebrtcPort, ApiPort = shared.ApiPort, StunUrl = shared.StunUrl,
      PublishUser = shared.PublishUser, PublishPass = shared.PublishPass,
      ReadUser = shared.ReadUser, ReadPass = shared.ReadPass,
    }, ct);
  }

  /// <summary>
  /// Takes a node out of the deployment, with its MediaMTX. Refused while cameras are on it: they
  /// would be left recording nowhere. Its credentials stay valid; revoking them needs a signing key
  /// per node (docs/future_plans/distributed-hardening.md).
  /// </summary>
  public async Task RemoveAsync(string serverId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var node = await db.MediaServerNodes.SingleOrDefaultAsync(n => n.ServerId == serverId, ct)
      ?? throw new RpcException(new Status(StatusCode.NotFound, $"there is no node '{serverId}'"));
    if (node.Configured)
      throw new RpcException(new Status(StatusCode.FailedPrecondition,
        $"'{serverId}' is listed in this core's configuration (MEDIA_SERVER_PAIRS) and cannot be removed here"));
    var cameras = await db.CameraSetups.CountAsync(c => c.ServerId == serverId, ct);
    if (cameras > 0)
      throw new RpcException(new Status(StatusCode.FailedPrecondition,
        $"{cameras} camera(s) are on '{serverId}': move them to another node or delete them first"));

    foreach (var mtx in (await storage.ListMediaMtxServersAsync(ct)).Where(s => s.ServerId == serverId))
      await storage.RemoveMediaMtxServerAsync(mtx.Id, ct);
    db.MediaServerNodes.Remove(node);
    await db.SaveChangesAsync(ct);
    logger.LogInformation("Node '{Name}' ({ServerId}) removed", node.Name, serverId);
  }
}
