using Npgsql;
using Vms.Cfg;

namespace VmsCfg;

public class StorageDb(NpgsqlDataSource dataSource)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;

  public async Task AddStorageAsync(Storage storage, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO storage (storage_id, root_path, reserved_bytes, disabled)
      VALUES ($1, $2, $3, $4)
      ON CONFLICT (storage_id) DO UPDATE SET
        root_path      = EXCLUDED.root_path,
        reserved_bytes = EXCLUDED.reserved_bytes,
        disabled       = EXCLUDED.disabled;
      """);
    command.Parameters.AddWithValue(storage.StorageId);
    command.Parameters.AddWithValue(storage.RootPath);
    command.Parameters.AddWithValue((long)storage.ReservedBytes);
    command.Parameters.AddWithValue(storage.Disabled);
    await command.ExecuteNonQueryAsync(ct);
  }

  /// <summary>
  /// Removes a storage, unless it is the last one.
  /// </summary>
  /// <remarks>
  /// What is protected is the last storage, not the seeded "default" one: once a second volume
  /// exists there is nothing special about the first, and refusing to delete it by name would
  /// leave an installation stuck with a path it has outgrown. Leaving zero storages is the case
  /// worth refusing — nothing records afterwards, and the failure is silent.
  ///
  /// Deleting a storage that cameras use is allowed, but it is not free: camera_storage cascades
  /// on the foreign key, so their assignments go with it and cannot be recovered. Storage.CameraCount
  /// exists so the operator is told that before confirming, and Disabled exists so a disk out for
  /// maintenance never has to be deleted at all.
  /// </remarks>
  public async Task<bool> RemoveStorageAsync(string storageId, CancellationToken ct)
  {
    await using (var count = _dataSource.CreateCommand("SELECT count(*) FROM storage;"))
    {
      if (Convert.ToInt64(await count.ExecuteScalarAsync(ct) ?? 0L) <= 1)
        throw new InvalidOperationException(
          "The last storage cannot be removed — without one nothing records. Disable it instead.");
    }

    await using var command = _dataSource.CreateCommand(
      "DELETE FROM storage WHERE storage_id = $1;");
    command.Parameters.AddWithValue(storageId);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  public async Task<List<Storage>> ListStoragesAsync(CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT s.storage_id, s.root_path, s.reserved_bytes, s.disabled,
             (SELECT count(*) FROM camera_storage cs WHERE cs.storage_id = s.storage_id)
      FROM storage s
      ORDER BY s.storage_id;
      """);

    var result = new List<Storage>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      result.Add(new Storage
      {
        StorageId     = reader.GetString(0),
        RootPath      = reader.GetString(1),
        ReservedBytes = (ulong)reader.GetInt64(2),
        Disabled      = reader.GetBoolean(3),
        CameraCount   = (uint)reader.GetInt64(4)
      });
    }
    return result;
  }

  public async Task SetCameraStoragesAsync(
    string cameraId,
    IReadOnlyList<CameraStorageAssignment> assignments,
    CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    await using (var del = new NpgsqlCommand(
      "DELETE FROM camera_storage WHERE camera_id = $1;", connection, tx))
    {
      del.Parameters.AddWithValue(cameraId);
      await del.ExecuteNonQueryAsync(ct);
    }

    foreach (var a in assignments)
    {
      await using var ins = new NpgsqlCommand("""
        INSERT INTO camera_storage (camera_id, storage_id, priority)
        VALUES ($1, $2, $3);
        """, connection, tx);
      ins.Parameters.AddWithValue(cameraId);
      ins.Parameters.AddWithValue(a.StorageId);
      ins.Parameters.AddWithValue(a.Priority);
      await ins.ExecuteNonQueryAsync(ct);
    }

    await tx.CommitAsync(ct);
  }

  public async Task<List<CameraStorageAssignment>> GetCameraStoragesAsync(
    string cameraId,
    CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT camera_id, storage_id, priority
      FROM camera_storage
      WHERE camera_id = $1
      ORDER BY priority ASC;
      """);
    command.Parameters.AddWithValue(cameraId);

    var result = new List<CameraStorageAssignment>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      result.Add(new CameraStorageAssignment
      {
        CameraId  = reader.GetString(0),
        StorageId = reader.GetString(1),
        Priority  = reader.GetInt32(2)
      });
    }
    return result;
  }
}
