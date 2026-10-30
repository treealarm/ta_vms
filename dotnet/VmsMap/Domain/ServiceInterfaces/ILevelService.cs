using System.Collections.Generic;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Domain
{
  public interface ILevelService
  {
    Task<List<string>> GetLevelsByZoom(double? zoom);
    Task<LevelDTO?> GetByZoomLevel(string name);
    Task<Dictionary<string, LevelDTO>> GetAllZooms();
  }
}
