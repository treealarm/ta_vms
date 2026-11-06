using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VmsFs;

public sealed class ClipsDbContext(DbContextOptions<ClipsDbContext> options) : DbContext(options)
{
  public DbSet<DbClip> Clips { get; set; }
  public DbSet<DbClipMetadataChunk> MetadataChunks { get; set; }

  // Camera and storage ids are UUID columns; the gRPC contract and this service's own code carry
  // them as strings (a UUID formatted as text). The conversion lives here, at the one boundary
  // where the database type matters. A clip without a storage is NULL in the column and on the
  // entity; the wire's Recording.storage_id keeps the empty string.
  private static readonly ValueConverter<string, Guid> UuidText =
    new(id => Guid.Parse(id), id => id.ToString());

  private static readonly ValueConverter<string?, Guid?> StorageId =
    new(id => string.IsNullOrEmpty(id) ? null : Guid.Parse(id),
        id => id == null ? null : id.Value.ToString());

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<DbClip>(e =>
    {
      e.Property(x => x.CameraId).HasConversion(UuidText);
      e.Property(x => x.StorageId).HasConversion(StorageId);
    });

    modelBuilder.Entity<DbClipMetadataChunk>(e =>
      e.Property(x => x.CameraId).HasConversion(UuidText));
  }
}
