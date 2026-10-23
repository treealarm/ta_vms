# A camera's new storage can take a minute and a half to reach its recorder

Found 2026-10-23, checking a Windows node of a Linux core (camera on `vms-win`).

A storage was added and assigned to the camera at 19:05:40 (`POST /api/storage`, then
`PUT /api/storage/set_camera_storages`); `vms-win` asked `vmsfs-win` for the camera's storages every
ten seconds and was told "no storage is assigned" until 19:07:16, when recording started. On a
Linux node the same two calls were picked up within ten seconds. `vmsfs-win` did receive
`storage-config-changed` at 19:05:40 ("storage caches dropped"); that `camera-storage-changed`
arrived too is likely (its queue had a consumer and nothing waiting) but not logged, the handler
logs at Debug.

96 seconds is one TTL of `VmsFs.CameraStorageCache` (one minute) after a poll that found nothing,
plus the background refresh. So something after the events put an empty list back into the cache.
`CameraStorageCache` has two ways to do that, and which one it was is not known:

- **A failed fetch is cached as "nothing assigned"**: `FetchAsync` turns any exception from VmsCfg
  into `[]` (`catch { return []; }`), and `GetAllAsync` caches it for the TTL. A cross-machine call
  that fails once is then reported to video_srv as a configuration problem, the wrong one.
- **An older fetch can overwrite an invalidation**: a fetch (or `StorageListCache` refresh) that
  started before the event stores its result after it, with nothing to tell that it is stale.

Both want the same shape: an invalidation bumps a version, a fetch stores its result only if the
version is still the one it started under, and a failed fetch stores nothing (the caller retries,
and logs the error).
