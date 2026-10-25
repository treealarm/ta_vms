# What is left of the log volume after the 2026-10-25 fix

Measured on a fresh Windows install (Windows 10, Russian locale) with the OnvifSim lab cameras,
then with the simulator stopped. The fix took the services off the host's default log providers,
gave OnvifLib a category of its own held at Information (no SOAP envelopes), quietened
IHttpClientFactory, wrote the redirected console in UTF-8, and made OnvifLib log an unreachable
camera without its stack (OnvifLib `OnvifErrors.Describe`). Ten PTZ status calls to a camera that is
off went from 316 KB of log to 20 KB; an idle install with no cameras from 288 KB per five minutes
to nothing. These are what it left.

- **A camera that is off is looked up from scratch on every call.** `CameraActor` evicts its
  `Camera` when service resolution fails, so the next call resolves again: about four
  `GetServices` attempts and three seconds per PTZ status call, each attempt an Error line. A
  short negative cache (the camera stays "unreachable" for some seconds) would answer at once and
  log once. Who calls it that often matters too: on the first test box something polled PTZ
  status every second or so.
- **video_srv logs every reconnect attempt at Error**, one line per stream every ~5 s while a
  camera is off: 69 KB per five minutes for four cameras, about 20 MB a day. Honest, but a
  doubling backoff in the log (not in the reconnect) would keep the first failures and drop the
  repeats.
- **web_srv does not use `AddVmsLogging`.** It logs through the host's console logger only, so it
  has no file under `VMS_LOG_PATH` and nothing of it reaches the Logs tab (VmsLogger). Its console
  is moderate (`Microsoft.AspNetCore` at Warning in appsettings.json), so this is about visibility,
  not volume.
- **The services still log at Debug** into their files and consoles; nothing measured on the
  bench was large at Debug once the SOAP dumps were gone, but it is the next thing to look at if a
  log fills again.
