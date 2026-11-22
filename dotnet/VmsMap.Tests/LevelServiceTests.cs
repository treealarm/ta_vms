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
  // The fixtures these tests share.
  private const string BuildingLevel = "building";

  private static VmsMapDbContext Create() =>
    new(new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options);

  [Fact]
  public async Task ANewLevelGetsAnIdAndAnExistingOneKeepsIt()
  {
    await using var db = Create();
    var levels = new LevelService(db);

    var first = new LevelDTO { zoom_level = BuildingLevel, zoom_min = 14, zoom_max = 16 };
    var created = await levels.UpdateAsync([first]);

    Assert.False(string.IsNullOrEmpty(created.Single().id));
    var stored = await db.Levels.SingleAsync();
    Assert.Equal(BuildingLevel, stored.zoom_level);
    Assert.Equal(14, stored.zoom_min);

    var renamed = new LevelDTO
    {
      id = created.Single().id,
      zoom_level = BuildingLevel,
      zoom_min = 15,
      zoom_max = 17,
    };
    var updated = await levels.UpdateAsync([renamed]);

    Assert.Equal(created.Single().id, updated.Single().id);
    Assert.Equal(15, (await db.Levels.SingleAsync()).zoom_min);
  }

  [Fact]
  public async Task RemovingALevelDropsOnlyThatRow()
  {
    await using var db = Create();
    var levels = new LevelService(db);

    var kept = (await levels.UpdateAsync([
      new LevelDTO { zoom_level = "city", zoom_min = 10, zoom_max = 12 }])).Single();
    var removed = (await levels.UpdateAsync([
      new LevelDTO { zoom_level = "rack", zoom_min = 18, zoom_max = 20 }])).Single();

    await levels.RemoveAsync([removed.id!]);

    var left = await db.Levels.ToListAsync();
    Assert.Single(left);
    Assert.Equal(kept.id, Utils.ConvertGuidToObjectId(left.Single().id));
  }

  // The dictionary is read by name, so two rows sharing one would make it unreadable: the save
  // says which name is duplicated instead of writing the pair.
  [Fact]
  public async Task TwoLevelsWithOneNameAreRefused()
  {
    await using var db = Create();
    var levels = new LevelService(db);

    await levels.UpdateAsync([new LevelDTO { zoom_level = BuildingLevel, zoom_min = 14, zoom_max = 16 }]);

    await Assert.ThrowsAsync<InvalidOperationException>(() => levels.UpdateAsync([
      new LevelDTO { zoom_level = BuildingLevel, zoom_min = 14, zoom_max = 16 },
      new LevelDTO { zoom_level = BuildingLevel, zoom_min = 17, zoom_max = 18 },
    ]));
    Assert.Single(await db.Levels.ToListAsync());
  }

  // A row written before that check existed must not take the whole list down with it.
  [Fact]
  public async Task ReadingTheDictionarySurvivesADuplicate()
  {
    await using var db = Create();
    db.Levels.Add(new DBLevel { id = Guid.NewGuid(), zoom_level = BuildingLevel, zoom_min = 14, zoom_max = 16 });
    db.Levels.Add(new DBLevel { id = Guid.NewGuid(), zoom_level = BuildingLevel, zoom_min = 17, zoom_max = 18 });
    await db.SaveChangesAsync();

    var dictionary = await new LevelService(db).GetAllZooms();

    Assert.Single(dictionary);
    Assert.Equal(BuildingLevel, dictionary.Single().Key);
  }
}
