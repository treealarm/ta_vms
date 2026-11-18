using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The by-id figure reads must not lie when the database does. A failed read reaches the caller —
// the gRPC layer logs it and answers with an error — while an id that names no figure stays an
// ordinary "nothing", because the map must draw the first and must not crash on the second.
public class GeoReadTests
{
  private static GeoService Create(out VmsMapDbContext db)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    db = new VmsMapDbContext(options);
    return new GeoService(db, new LevelService(db));
  }

  [Fact]
  public async Task AFailedReadOfSeveralFiguresReachesTheCaller()
  {
    var geo = Create(out var db);
    var id = Guid.NewGuid().ToString();
    db.Dispose();

    await Assert.ThrowsAnyAsync<Exception>(() => geo.GetGeoObjectsAsync([id]));
  }

  [Fact]
  public async Task AFailedReadOfOneFigureReachesTheCaller()
  {
    var geo = Create(out var db);
    var id = Guid.NewGuid().ToString();
    db.Dispose();

    await Assert.ThrowsAnyAsync<Exception>(() => geo.GetGeoObjectAsync(id));
  }

  [Theory]
  [InlineData("")]
  [InlineData("not-a-guid")]
  public async Task AnIdThatNamesNoFigureIsNotAFailure(string id)
  {
    var geo = Create(out _);

    Assert.Null(await geo.GetGeoObjectAsync(id));
  }

  [Fact]
  public async Task NoIdsIsAnEmptyAnswerNotAFailure()
  {
    var geo = Create(out _);

    Assert.Empty(await geo.GetGeoObjectsAsync([]));
  }
}
