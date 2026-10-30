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

    public async Task<DBFile> PutAsync(string? mime, byte[] bytes)
    {
      if (bytes.Length > MaxFileBytes)
      {
        throw new ArgumentException(
          $"A file may not exceed {MaxFileBytes} bytes", nameof(bytes));
      }

      var file = new DBFile
      {
        id = Utils.NewGuid(),
        mime = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime,
        bytes = bytes,
        etag = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
      };

      _db.Files.Add(file);
      await _db.SaveChangesAsync();
      return file;
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
