using Microsoft.EntityFrameworkCore;

namespace VmsFs;

public sealed class ClipsDbContext(DbContextOptions<ClipsDbContext> options) : DbContext(options)
{
  public DbSet<DbClip> Clips { get; set; }
}
