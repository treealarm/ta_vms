using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // Object states and the dictionary that says which of them are alarms. The seeding of the
  // default dictionary lives in the schema migration, not here: a replicable service must not
  // race itself into two "default" sets on first start.
  internal class StateService : IStateService
  {
    private readonly VmsMapDbContext _dbContext;

    public StateService(VmsMapDbContext dbContext)
    {
      _dbContext = dbContext;
    }

    public async Task<long> UpdateStatesAsync(List<ObjectStateDTO> newObjs)
    {
      if (newObjs.Count == 0)
        return 0;

      var idsToUpdate = newObjs
          .Select(x => Utils.ConvertObjectIdToGuid(x.id))
          .ToList();

      var existing = await _dbContext.ObjectStates
          .Include(x => x.states)
          .Where(x => idsToUpdate.Contains(x.id))
          .ToListAsync();

      var existingDict = existing.ToDictionary(x => x.id);

      var toUpdate = new List<DBObjectState>();
      var toAdd = new List<DBObjectState>();

      foreach (var dto in newObjs)
      {
        var objId = Utils.ConvertObjectIdToGuid(dto.id) ?? Guid.Empty;

        var states = (dto.states ?? new List<string>()).Select(s => new DBObjectStateValue
        {
          state = s,
          object_id = objId
        }).ToList();

        if (existingDict.TryGetValue(objId, out var existingEntity))
        {
          // The DTO carries the full current set, so the stored values are replaced, not merged.
          existingEntity.states.Clear();
          existingEntity.timestamp = DateTime.UtcNow;
          existingEntity.states.AddRange(states);

          toUpdate.Add(existingEntity);
        }
        else
        {
          toAdd.Add(new DBObjectState
          {
            id = objId,
            timestamp = DateTime.UtcNow,
            states = states
          });
        }
      }

      _dbContext.ObjectStates.UpdateRange(toUpdate);
      await _dbContext.ObjectStates.AddRangeAsync(toAdd);
      return await _dbContext.SaveChangesAsync();
    }

    public async Task<long> UpdateStateDescrsAsync(List<ObjectStateDescriptionDTO> newObjs)
    {
      if (newObjs.Count == 0)
        return 0;

      var names = newObjs.Select(x => x.state).Where(s => !string.IsNullOrEmpty(s)).ToList();
      var existing = await _dbContext.ObjectStateDescriptions
          .Where(x => names.Contains(x.state))
          .ToDictionaryAsync(x => x.state);

      foreach (var dto in newObjs)
      {
        if (string.IsNullOrEmpty(dto.state))
        {
          throw new ArgumentException("A state description needs a state name");
        }

        if (existing.TryGetValue(dto.state, out var entity))
        {
          entity.state_descr = dto.state_descr;
          entity.state_color = dto.state_color;
          entity.alarm = dto.alarm ?? false;
        }
        else
        {
          _dbContext.ObjectStateDescriptions.Add(new DBObjectStateDescription
          {
            // Square assigned Guid.Empty when the caller left the id out, and two new states in
            // one call then collided on the primary key.
            id = Utils.ConvertObjectIdToGuid(dto.id) ?? Utils.NewGuid(),
            state = dto.state,
            state_descr = dto.state_descr,
            state_color = dto.state_color,
            alarm = dto.alarm ?? false
          });
        }
      }

      return await _dbContext.SaveChangesAsync();
    }

    public async Task<Dictionary<string, ObjectStateDTO>> GetStatesAsync(List<string> ids)
    {
      var parsedIds = ids.Select(Utils.ConvertObjectIdToGuid).ToList();

      var states = await _dbContext.ObjectStates
          .Include(x => x.states)
          .Where(x => parsedIds.Contains(x.id))
          .ToListAsync();

      return states.ToDictionary(
          x => Utils.ConvertGuidToObjectId(x.id),
          x => new ObjectStateDTO
          {
            id = Utils.ConvertGuidToObjectId(x.id),
            states = x.states.Select(s => s.state).ToList(),
            timestamp = x.timestamp,
            Version = x.Version
          });
    }

    public async Task<List<ObjectStateDescriptionDTO>> GetStateDescrAsync(List<string> states)
    {
      var result = await _dbContext.ObjectStateDescriptions
          .Where(x => states.Contains(x.state))
          .ToListAsync();

      return result.Select(x => new ObjectStateDescriptionDTO
      {
        id = Utils.ConvertGuidToObjectId(x.id),
        state = x.state,
        state_descr = x.state_descr,
        state_color = x.state_color,
        alarm = x.alarm
      }).ToList();
    }

    public async Task<List<AlarmState>> GetAlarmedLeafStatesAsync(List<string> ids)
    {
      var parsedIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(x => x.HasValue)
          .Select(x => x!.Value)
          .ToList();

      if (parsedIds.Count == 0)
        return new List<AlarmState>();

      var result = await _dbContext.AlarmStates
          .Where(x => parsedIds.Contains(x.id) && x.alarm)
          .ToListAsync();

      return result.Select(x => new AlarmState
      {
        id = Utils.ConvertGuidToObjectId(x.id),
        alarm = x.alarm,
        children_alarms = x.children_alarms
      }).ToList();
    }

    public async Task<Dictionary<string, AlarmState>> GetAlarmStatesAsync(List<string> ids)
    {
      var parsedIds = ids.Select(Utils.ConvertObjectIdToGuid).ToList();

      var result = await _dbContext.AlarmStates
          .Where(x => parsedIds.Contains(x.id))
          .ToListAsync();

      return result.ToDictionary(
          x => Utils.ConvertGuidToObjectId(x.id),
          x => new AlarmState
          {
            id = Utils.ConvertGuidToObjectId(x.id),
            alarm = x.alarm,
            children_alarms = x.children_alarms
          });
    }

    public async Task<Dictionary<string, ObjectStateDescriptionDTO>> GetAlarmStatesDescr(List<string>? states)
    {
      var query = _dbContext.ObjectStateDescriptions.Where(x => x.alarm);

      if (states != null && states.Count > 0)
      {
        query = query.Where(x => states.Contains(x.state));
      }

      var list = await query.ToListAsync();

      return list.ToDictionary(
          x => x.state,
          x => new ObjectStateDescriptionDTO
          {
            id = Utils.ConvertGuidToObjectId(x.id),
            state = x.state,
            state_descr = x.state_descr,
            state_color = x.state_color,
            alarm = x.alarm
          });
    }

    public async Task<Dictionary<string, ObjectStateDTO>> GetAlarmedStates(List<string>? statesFilter)
    {
      var alarmedDescr = await GetAlarmStatesDescr(statesFilter);
      var alarmedStates = alarmedDescr.Keys.ToHashSet();

      var result = await _dbContext.ObjectStates
          .Include(x => x.states)
          .Where(x => x.states.Any(s => alarmedStates.Contains(s.state)))
          .ToListAsync();

      return result.ToDictionary(
          x => Utils.ConvertGuidToObjectId(x.id),
          x => new ObjectStateDTO
          {
            id = Utils.ConvertGuidToObjectId(x.id),
            states = x.states.Select(s => s.state).ToList()
          });
    }
  }
}
