using Microsoft.EntityFrameworkCore;

namespace VmsFs;

public sealed class ClipsDbContext(DbContextOptions<ClipsDbContext> options) : DbContext(options)
{
  public DbSet<DbClip> Clips { get; set; }
  public DbSet<DbClipMetadataChunk> MetadataChunks { get; set; }
}
