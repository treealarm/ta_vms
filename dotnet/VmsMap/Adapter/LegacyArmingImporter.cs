using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Adapter
{
  /// <summary>Whether the one-time move of VmsCfg's arming columns into the map has run.</summary>
  public interface ILegacyImportMarker
  {
    Task<bool> IsImportedAsync(CancellationToken ct);
    Task MarkImportedAsync(CancellationToken ct);
  }

  /// <summary>
  /// The state store is shared by every replica; the key is the whole marker, so a restarted or a
  /// second replica does not walk the deployment again.
  /// </summary>
  internal sealed class StateStoreImportMarker(DaprClient dapr, string stateStoreName) : ILegacyImportMarker
  {
    private const string Key = "object-settings-seeded";

    public async Task<bool> IsImportedAsync(CancellationToken ct)
    {
      var value = await dapr.GetStateAsync<string>(stateStoreName, Key, cancellationToken: ct);
      return !string.IsNullOrEmpty(value);
    }

    public Task MarkImportedAsync(CancellationToken ct) =>
      dapr.SaveStateAsync(stateStoreName, Key, "1", cancellationToken: ct);
  }

  /// <summary>
  /// Carries a camera's arming mode and a digital input's alarm rule from VmsCfg into the map once,
  /// before the arming sweep runs: without it a camera that was explicitly armed would look like
  /// `inherit` for a moment and could be written back to the zone's answer.
  /// </summary>
  /// <remarks>
  /// The rows are seeded, not overwritten: anything the map already holds is a later decision. A
  /// page that VmsCfg cannot answer aborts the pass without the marker, and the next start retries
  /// — every write is idempotent. This is the migration path only; the legacy columns go away once
  /// every installation has started on this release.
  /// </remarks>
  internal sealed class LegacyArmingImporter(
    DeviceObjectSettingsStore settings,
    ICameraStateSource cameraState,
    IDeviceSettingsSource legacySettings,
    DeviceNodeService nodes,
    ILegacyImportMarker marker,
    ILogger<LegacyArmingImporter> logger)
  {
    private const int PageSize = 200;

    public async Task EnsureImportedAsync(IReadOnlyList<CameraInfo> cameras, CancellationToken ct)
    {
      if (await marker.IsImportedAsync(ct))
        return;

      var imported = 0;
      foreach (var page in cameras.Chunk(PageSize))
      {
        var ids = page.Select(c => c.CameraId).ToList();

        IReadOnlyDictionary<string, CameraArmState> states;
        IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>> legacy;
        try
        {
          states = await cameraState.GetStatesAsync(ids, ct);
          legacy = await legacySettings.ListManyAsync(ids, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          logger.LogWarning(ex,
            "Importing the arming settings from VmsCfg failed; the next start retries");
          return;
        }

        foreach (var cameraId in ids)
        {
          var mode = states.GetValueOrDefault(cameraId)?.ArmMode;
          if (IsExplicit(mode))
          {
            await settings.SeedAsync(
              CameraObjectId.For(cameraId), mode!, null, false, ct);
            imported += 1;
          }

          foreach (var part in legacy.GetValueOrDefault(cameraId) ?? [])
          {
            if (!string.Equals(
                  part.Kind, DeviceObjectId.DigitalInputPart, StringComparison.Ordinal))
              continue;
            if (string.Equals(part.ArmMode, ArmMode.Inherit, StringComparison.Ordinal)
                && part.TriggerOn is null && !part.AlarmEnabled)
              continue;

            var nodeId = await nodes.EnsureAsync(
              DeviceObjectId.CameraSource, cameraId, part.Kind, part.Token, ct);
            if (nodeId is null)
            {
              logger.LogWarning(
                "The input '{CameraId}/{Token}' has no node to carry its settings", cameraId, part.Token);
              continue;
            }

            await settings.SeedAsync(
              nodeId, part.ArmMode, part.TriggerOn, part.AlarmEnabled, ct);
            imported += 1;
          }
        }
      }

      await marker.MarkImportedAsync(ct);
      logger.LogInformation("Imported {Count} arming setting(s) from VmsCfg", imported);
    }

    private static bool IsExplicit(string? mode) =>
      string.Equals(mode, ArmMode.Armed, StringComparison.Ordinal)
      || string.Equals(mode, ArmMode.Disarmed, StringComparison.Ordinal);
  }
}
