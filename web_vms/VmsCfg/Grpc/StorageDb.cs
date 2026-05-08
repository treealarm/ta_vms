using Npgsql;
using Vms.Cfg;

namespace VmsCfg;

public class StorageDb(NpgsqlDataSource dataSource)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;

  public async Task AddStorageAsync(Storage storage, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO storage (storage_id, root_path, reserved_bytes)
      VALUES ($1, $2, $3)
      ON CONFLICT (storage_id) DO UPDATE SET
        root_path      = EXCLUDED.root_path,
        reserved_bytes = EXCLUDED.reserved_bytes;
      """);
    command.Parameters.AddWithValue(storage.StorageId);
    command.Parameters.AddWithValue(storage.RootPath);
    command.Parameters.AddWithValue((long)storage.ReservedBytes);
    await command.ExecuteNonQueryAsync(ct);
  }

  public async Task<bool> RemoveStorageAsync(string storageId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM storage WHERE storage_id = $1;");
    command.Parameters.AddWithValue(storageId);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  public async Task<List<Storage>> ListStoragesAsync(CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT storage_id, root_path, reserved_bytes
      FROM storage
      ORDER BY storage_id;
      """);

    var result = new List<Storage>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      result.Add(new Storage
      {
        StorageId     = reader.GetString(0),
        RootPath      = reader.GetString(1),
        ReservedBytes = (ulong)reader.GetInt64(2)
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
