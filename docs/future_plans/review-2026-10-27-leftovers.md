# What the 2026-10-27 review left open

The review of the week before 2026-10-27 (`afb2294..3cb1086`) found nothing that brings a service
down. Its enrollment, package, password-change and shutdown findings were fixed the same day
(`e4e3314..c3aadf3`); the RabbitMQ consumers after an hour of 15-minute subscription renewals, and
`vmsonvif`'s sidecar starting both subscriptions at once, were checked on the Windows core
192.168.1.82 and are fine (one consumer per queue, the connection count flat, 1.2 s of CPU in 43
minutes). What is left:

## Edge import: how a replay ends

- `video_srv/edge_import/onvif_replay_rtsp.cpp`, the silence check in the interleaved read: idle
  after media counts as the end of the range (`AVERROR_EOF`) only once `m_have_base_ts` is set, and
  that is set in `take_picture`, when the first finished picture is handed out, no longer at the
  first VCL NAL. A camera without the RTP marker bit that sends one picture and goes silent ends
  the import with `EIO` instead of `EOF`. A flag for "media seen", set where video RTP arrives,
  would say what the check means.
- The same file, `m_end_rc` in the read loop: one broken interleaved frame (length 0, or more than
  65535) ends the stream for good with `AVERROR_INVALIDDATA`. It did before as well; not a
  regression, but a camera that sends one such frame loses the rest of the range.

Neither was tried against a camera; both need one with an archive (or OnvifSim's replay).

## To check on a live stack

- `camera_manager::stop_all` (`video_srv/camera_manager.cpp`) shuts every `cam_device` down at once,
  each in a `jthread`. The devices are independent, but stop vms once with ten cameras or so and
  look for a race in what they share (`fs_client`, `onvif_metadata_client`, the logger).
- web_srv's HTTPS on Windows (`TlsEndpoint`, the re-import through PKCS#12 for SChannel) has run on
  Linux only; ta_install does not set `WEB_VMS_TLS_CERT`/`WEB_VMS_TLS_KEY` yet (step 4 of the core's
  TLS).

## Small

- `FrontendConfigController.UserAdminPort`: a bad `MT_ADMIN_PORT` throws from the static
  initializer. With Keycloak, `Program.cs` touches the class at startup and the process stops;
  without it, the first `GET /api/config/frontend` is a 500 instead.
