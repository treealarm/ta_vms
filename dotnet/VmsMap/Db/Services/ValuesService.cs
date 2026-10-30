using VmsMap.Domain;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace VmsMap.Db;

internal class ValuesService : IValuesService, IValuesServiceInternal
{
  private readonly VmsMapDbContext _dbContext;

  public ValuesService(VmsMapDbContext dbContext)
  {
    _dbContext = dbContext;
  }

  public async Task<Dictionary<string, ValueDTO>> GetListByIdsAsync(List<string> ids)
  {
    var guids = ids.Select(Utils.ConvertObjectIdToGuid).Where(g => g != null).ToList();
    var entities = await _dbContext.Values
      .Where(v => guids.Contains(v.id))
      .ToListAsync();

    return entities.ToDictionary(
      v => Utils.ConvertGuidToObjectId(v.id),
      v => ConvertDB2DTO(v));
  }

  public async Task UpdateListAsync(List<ValueDTO> valuesToUpdate)
  {
    if (valuesToUpdate.Count == 0)
    {
      return;
    }

    var ids = valuesToUpdate
      .Select(v => Utils.ConvertObjectIdToGuid(v.id))
      .Where(g => g != null)
      .Select(g => g!.Value)
      .ToList();

    var existing = await _dbContext.Values
      .Where(v => ids.Contains(v.id))
      .ToDictionaryAsync(v => v.id);

    foreach (var dto in valuesToUpdate)
    {
      var entity = ConvertDTO2DB(dto);

      if (existing.TryGetValue(entity.id, out var row))
      {
        row.owner_id = entity.owner_id;
        row.name = entity.name;
        row.value = entity.value;
      }
      else
      {
        // Square called Update() on every DTO, so a value without an id was marked Modified and
        // the insert failed as a concurrency error on a row that never existed.
        _dbContext.Values.Add(entity);
      }

      dto.id = Utils.ConvertGuidToObjectId(entity.id);
    }

    await _dbContext.SaveChangesAsync();
  }

  public async Task RemoveAsync(List<string> ids)
  {
    var guids = ids.Select(Utils.ConvertObjectIdToGuid).Where(g => g != null).ToList();
    var toRemove = await _dbContext.Values
      .Where(v => guids.Contains(v.id))
      .ExecuteDeleteAsync();
  }

  public async Task<Dictionary<string, ValueDTO>> GetListByOwnersAsync(List<string> owners)
  {
    var guids = owners.Select(id => Utils.ConvertObjectIdToGuid(id)).ToList();

    var entities = await _dbContext.Values
      .Where(v => guids.Contains(v.owner_id))
      .ToListAsync();

    return entities.ToDictionary(
      v => Utils.ConvertGuidToObjectId(v.id),
      v => ConvertDB2DTO(v));
  }

  public static DBValue ConvertDTO2DB(ValueDTO dto)
  {
    return new DBValue
    {
      id = Utils.ConvertObjectIdToGuid(dto.id) ?? Guid.NewGuid(),
      name = dto.name,
      owner_id = Utils.ConvertObjectIdToGuid(dto.owner_id) ?? Guid.NewGuid(),
      value = dto.value == null ? null : JsonSerializer.Serialize(dto.value)
    };
  }

  public static ValueDTO ConvertDB2DTO(DBValue dbo)
  {
    return new ValueDTO
    {
      id = Utils.ConvertGuidToObjectId(dbo.id),
      name = dbo.name,
      owner_id = Utils.ConvertGuidToObjectId(dbo.owner_id),
      value = dbo.value == null ? null : JsonSerializer.Deserialize<object>(dbo.value),
      Version = dbo.Version
    };
  }

  public async Task<Dictionary<string, ValueDTO>> UpdateValuesFilteredByNameAsync(List<ValueDTO> values)
  {
    var result = new Dictionary<string, ValueDTO>();

    var ownerGuids = values
      .Select(dto => Utils.ConvertObjectIdToGuid(dto.owner_id) ?? Guid.NewGuid())
      .ToList();

    var distinctOwnerGuids = ownerGuids.Distinct().ToList();

    // Один запрос вместо одного FirstOrDefaultAsync на каждый элемент батча (был N+1).
    var existingByKey = await _dbContext.Values
      .Where(v => distinctOwnerGuids.Contains(v.owner_id))
      .ToDictionaryAsync(v => (v.owner_id, v.name));

    for (int i = 0; i < values.Count; i++)
    {
      var dto = values[i];
      var ownerGuid = ownerGuids[i];
      var key = (ownerGuid, dto.name);

      if (existingByKey.TryGetValue(key, out var existing))
      {
        existing.value = dto.value == null ? null : JsonSerializer.Serialize(dto.value);
        _dbContext.Values.Update(existing);

        result[existing.id.ToString()] = new ValueDTO
        {
          id = existing.id.ToString(),
          name = existing.name,
          owner_id = Utils.ConvertGuidToObjectId(existing.owner_id),
          value = dto.value
        };
      }
      else
      {
        var newId = Guid.NewGuid();
        var newValue = new DBValue
        {
          id = newId,
          name = dto.name,
          owner_id = ownerGuid,
          value = dto.value == null ? null : JsonSerializer.Serialize(dto.value)
        };

        _dbContext.Values.Add(newValue);
        existingByKey[key] = newValue;

        result[newId.ToString()] = new ValueDTO
        {
          id = newId.ToString(),
          name = newValue.name,
          owner_id = Utils.ConvertGuidToObjectId(ownerGuid),
          value = dto.value
        };
      }
    }

    await _dbContext.SaveChangesAsync();
    return result;
  }


}
