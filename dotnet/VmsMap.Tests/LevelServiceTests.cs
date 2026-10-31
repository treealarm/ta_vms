using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The zoom-level dictionary's write half: the editor adds a layer or moves its zoom range, and
// removes a layer it no longer wants. The table is seeded by the migration, so these run on an
// empty in-memory one.
public class LevelServiceTests
{
  private static VmsMapDbContext Create() =>
    new(new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options);

  [Fact]
  public async Task ANewLevelGetsAnIdAndAnExistingOneKeepsIt()
  {
    await using var db = Create();
    var levels = new LevelService(db);

    var first = new LevelDTO { zoom_level = "building", zoom_min = 14, zoom_max = 16 };
    var created = await levels.UpdateAsync([first]);

    Assert.False(string.IsNullOrEmpty(created[0].id));
    var stored = await db.Levels.SingleAsync();
    Assert.Equal("building", stored.zoom_level);
    Assert.Equal(14, stored.zoom_min);

    var renamed = new LevelDTO { id = created[0].id, zoom_level = "building", zoom_min = 15, zoom_max = 17 };
    var updated = await levels.UpdateAsync([renamed]);

    Assert.Equal(created[0].id, updated[0].id);
    Assert.Equal(15, (await db.Levels.SingleAsync()).zoom_min);
  }

  [Fact]
  public async Task RemovingALevelDropsOnlyThatRow()
  {
    await using var db = Create();
    var levels = new LevelService(db);

    var kept = (await levels.UpdateAsync([new LevelDTO { zoom_level = "city", zoom_min = 10, zoom_max = 12 }]))[0];
    var removed = (await levels.UpdateAsync([new LevelDTO { zoom_level = "rack", zoom_min = 18, zoom_max = 20 }]))[0];

    await levels.RemoveAsync([removed.id!]);

    var left = await db.Levels.ToListAsync();
    Assert.Single(left);
    Assert.Equal(kept.id, Utils.ConvertGuidToObjectId(left[0].id));
  }
}
