using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  /// <summary>
  /// Reconciles a camera from a <c>device-snapshot</c>: the manifest of its parts and their state,
  /// which is also how a transition the map lost while it was down is found again.
  /// </summary>
  /// <remarks>
  /// The snapshot passes the same ordering guard as an event. It has no sequence number, so within
  /// one epoch it is accepted as the newest thing that process said; across epochs only its own
  /// <c>observed_at</c> decides. A snapshot that disagrees with the stored state is exactly a lost
  /// event: the state is written and the journal gets one, with a deterministic id derived from the
  /// snapshot so a redelivery is a no-op.
  /// <para>
  /// A part that disappears from the manifest is marked <c>present=false</c>; its node, state and
  /// history stay. A camera that did not answer at all only has <c>reachable</c> changed — nothing
  /// is marked absent by an unanswered call.
  /// </para>
  /// </remarks>
  internal sealed class DeviceEventReconciler
  {
    private readonly VmsMapDbContext _db;
    private readonly DeviceNodeService _nodes;
    private readonly ObjectStateWriter _stateWriter;
    private readonly EventsWriter _eventsWriter;
    private readonly DeviceEventTranslator _translator;
    private readonly ILogger<DeviceEventReconciler> _logger;

    public DeviceEventReconciler(
      VmsMapDbContext db,
      DeviceNodeService nodes,
      ObjectStateWriter stateWriter,
      EventsWriter eventsWriter,
      DeviceEventTranslator translator,
      ILogger<DeviceEventReconciler> logger)
    {
      _db = db;
      _nodes = nodes;
      _stateWriter = stateWriter;
      _eventsWriter = eventsWriter;
      _translator = translator;
      _logger = logger;
    }

    public async Task ApplyAsync(DeviceSnapshotMessage message, CancellationToken ct)
    {
      if (!string.Equals(message.SourceKind, DeviceObjectId.CameraSource, StringComparison.Ordinal)
          || string.IsNullOrWhiteSpace(message.SourceId))
      {
        return;
      }

      var cameraId = message.SourceId!;

      // A message without a parts list is malformed; treating it as "no parts" would mark every one
      // of them absent.
      if (message.Parts is null)
      {
        _logger.LogWarning("Dropped a device-snapshot for camera {CameraId} without a parts list", cameraId);
        return;
      }

      var partRefs = message.Parts
        .Where(p => !string.IsNullOrWhiteSpace(p.Kind) && !string.IsNullOrWhiteSpace(p.Token))
        .Where(p => DevicePayload.IsDigitalInput(p.Kind) || DevicePayload.IsRelay(p.Kind))
        .Select(p => (Kind: p.Kind!, Token: p.Token!))
        .Distinct()
        .ToList();

      var listed = partRefs.ToHashSet();

      // The manifest is brought in line before the state transaction opens: one pass for the whole
      // camera (two calls to VmsCfg, not per part), and no database lock is held across the network.
      // It is idempotent, so a sweep that dies here is completed by the next one.
      if (message.Reachable)
        await _nodes.EnsurePartsAsync(cameraId, partRefs, ct);

      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      var rows = await _db.DeviceNodes
        .Where(n => n.source_kind == DeviceObjectId.CameraSource && n.source_id == cameraId)
        .ToListAsync(ct);

      if (!message.Reachable)
      {
        // Unreachable is not absent: only the visibility flag changes, and no alarm is closed.
        foreach (var row in rows)
          row.reachable = false;

        await _db.SaveChangesAsync(ct);
        if (tx is not null)
          await tx.CommitAsync(ct);
        return;
      }

      foreach (var part in message.Parts)
      {
        if (part.Kind is null || part.Token is null)
          continue;

        var row = rows.FirstOrDefault(r =>
          string.Equals(r.part_kind, part.Kind, StringComparison.Ordinal)
          && string.Equals(r.part_token, part.Token, StringComparison.Ordinal));
        if (row is null)
          continue;

        row.reachable = true;
        row.present = part.Present;

        if (!part.Present)
          continue; // absent parts keep their last known state and history

        if (!DeviceCursor.Accept(
              row.last_epoch, row.last_seq, row.last_observed_at,
              message.Epoch, seq: null, message.ObservedAt))
        {
          continue;
        }

        var objectId = Utils.ConvertGuidToObjectId(row.object_id);

        var synthetic = new DeviceEventMessage(
          SourceKind: DeviceObjectId.CameraSource,
          SourceId: cameraId,
          PartKind: part.Kind,
          Token: part.Token,
          Contact: part.Contact,
          Active: part.Active,
          Previous: null,
          Initialized: false,
          ObservedAt: message.ObservedAt,
          Epoch: message.Epoch,
          Seq: 0,
          PolarityKnown: DevicePayload.IsRelay(part.Kind)
                         || DevicePayload.Text(part.Contact) is not null);

        var change = _translator.Translate(synthetic);
        if (change is not null && await _stateWriter.SetStatesAsync(objectId, [change.State], ct))
        {
          await _eventsWriter.WriteAsync([
            new EventDTO
            {
              id = EventIds.ForSnapshot(
                DeviceObjectId.CameraSource,
                cameraId,
                part.Kind,
                part.Token,
                message.Epoch!,
                message.ObservedAt),
              object_id = objectId,
              event_name = change.EventName,
              event_priority = 0,
              timestamp = DateTime.UtcNow,
              param0 = change.Param0,
              extra_props = [.. change.ExtraProps],
            }
          ], ct);
        }
        else if (change is not null)
        {
          // The condition may still be live while the state is not the one on record. Raising the
          // alarm entity is M2.4c; this is the hook it will use.
          await ReconcileAlarmAsync(objectId, change.State, ct);
        }

        DeviceCursor.Advance(
          setEpoch: value => row.last_epoch = value,
          setSeq: value => row.last_seq = value,
          setObservedAt: value => row.last_observed_at = value,
          epoch: message.Epoch!,
          seq: null,
          observedAt: message.ObservedAt);
      }

      // Everything the camera no longer lists is absent, but its node and history stay.
      foreach (var row in rows)
      {
        row.reachable = true;
        if (!listed.Contains((row.part_kind, row.part_token)))
          row.present = false;
      }

      await _db.SaveChangesAsync(ct);
      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    // M2.4c owns the alarm entity. Until it exists nothing is raised or closed here; the snapshot
    // still keeps the state and the journal honest.
    private Task ReconcileAlarmAsync(string objectId, string state, CancellationToken ct) =>
      Task.CompletedTask;
  }
}
