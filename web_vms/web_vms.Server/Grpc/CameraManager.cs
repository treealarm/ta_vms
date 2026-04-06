namespace web_vms
{
  using System.Collections.Concurrent;

  public class CameraManager : ICameraManager
  {
    private readonly ConcurrentDictionary<string, CameraStateDto> _cameras = new();
    private readonly CameraStorage _storage = new();

    public CameraManager()
    {
      // загрузка при старте
      var data = _storage.Load();
      foreach (var c in data)
        _cameras[c.CameraId] = c;
    }

    public Task<bool> Add(CameraStateDto dto)
    {
      _cameras[dto.CameraId] = dto;
      Persist();
      return Task.FromResult(true);
    }

    public Task<bool> Remove(string id)
    {
      var removed = _cameras.TryRemove(id, out _);
      Persist();
      return Task.FromResult(removed);
    }

    public Task<List<CameraStateDto>> List()
    {
      return Task.FromResult(_cameras.Values.ToList());
    }

    public Task<bool> SetState(CameraStateDto dto)
    {
      if (!_cameras.ContainsKey(dto.CameraId))
        return Task.FromResult(false);

      _cameras[dto.CameraId] = dto;
      Persist();
      return Task.FromResult(true);
    }

    public Task<CameraStateDto?> Get(string id)
    {
      _cameras.TryGetValue(id, out var cam);
      return Task.FromResult(cam);
    }

    private void Persist()
    {
      _storage.Save(_cameras.Values);
    }
  }
}
