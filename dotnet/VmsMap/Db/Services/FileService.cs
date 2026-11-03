using Microsoft.EntityFrameworkCore;
using System;
using VmsMap.Domain;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace VmsMap.Db
{
  // Blobs in the database. The service is replicable, so a file on a local volume would be
  // missing (or different) on the next instance that served the row.
  public class FileService
  {
    public const int MaxFileBytes = 20 * 1024 * 1024;

    private readonly VmsMapDbContext _db;

    public FileService(VmsMapDbContext db)
    {
      _db = db;
    }

    public Task<DBFile> PutAsync(string? mime, byte[] bytes) => PutAsync(Utils.NewGuid(), mime, bytes);

    /// Writes a blob under an id the caller chose, replacing the row with that id. The demo world
    /// derives its file ids from its own keys, so a reinstall finds the row it wrote last time
    /// instead of leaving the old picture behind as an orphan.
    public async Task<DBFile> PutAsync(Guid id, string? mime, byte[] bytes)
    {
      if (bytes.Length > MaxFileBytes)
      {
        throw new ArgumentException(
          $"A file may not exceed {MaxFileBytes} bytes", nameof(bytes));
      }

      var etag = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
      var row = await _db.Files.FirstOrDefaultAsync(f => f.id == id);

      if (row is null)
      {
        row = new DBFile { id = id };
        _db.Files.Add(row);
      }

      row.mime = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime;
      row.bytes = bytes;
      row.etag = etag;
      await _db.SaveChangesAsync();
      return row;
    }

    public Task<DBFile?> GetAsync(string id)
    {
      var guid = Utils.ConvertObjectIdToGuid(id);
      if (guid == null)
      {
        return Task.FromResult<DBFile?>(null);
      }

      return _db.Files.AsNoTracking()
        .FirstOrDefaultAsync(f => f.id == guid.Value);
    }
  }
}
