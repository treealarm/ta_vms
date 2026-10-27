# Hardware encoding on Windows without an NVIDIA card

Found 2026-10-26, while moving NVENC on Windows from CUDA to Direct3D 11 (roi_transcode,
`Engine::Nvenc` and `DecodeEngine::D3d11va`). The decode half of a vendor-neutral Windows path is
there: D3D11VA, in the Windows FFmpeg already (vcpkg builds it with `--enable-d3d11va`), on any
vendor's GPU. Without a hardware encoder every HEVC stream goes to `video_transcode_processor`,
decode included — 121–135% of a core per 1080p25 stream on the i5-750
(`../ta_install/windows/FINDINGS.md`).

**QSV through D3D11, for Intel, is in** (2026-10-26): `ffmpeg[qsv]` in the Windows vcpkg ports,
the QSV backend on a D3D11 device on the Intel adapter with the decoder handed that device, and
`IntelOneVpl` after `Nvenc` in `video_srv`'s default order. What is left of it:

- **Checking it on Windows.** The same code is checked on Linux, where QSV runs on VA-API: on the
  i7-12700's UHD 770 a Transcoder session decodes on the GPU, uploads nothing and puts every
  picture in its place (roi_transcode `transcode_device_test`). On Windows only the device type
  differs — D3D11 on the adapter with vendor id `0x8086`.

  **Checked so far** (2026-10-26, i7-12700 / UHD 770, Windows 11 26200, Intel graphics driver
  30.0.101.1371 of February 2022 — not updated, on purpose), on the `windows-build` asset of
  `d596529`:
  - `vms.exe` started as CI starts it: every DLL loads, it logs the order
    `vaapi -> nvenc -> qsv` and stops at `DAPR_GRPC_PORT`.
  - `h264_qsv` through the shipped FFmpeg (9.0.1) and `libvpl.dll`, with
    `scripts/win/qsv-probe.py`: D3D11VA and QSV devices open on `8086:4680`, the driver's oneVPL
    runtime reports implementation 2.6, and the encoder reports *"Initialized an internal MFX
    session using hardware accelerated implementation"*. 300 frames of 1080p NV12 from system
    memory, H.264 High at 4 Mbit/s: 300 packets in 1.5 s, about 200 fps. So a 2022 driver is
    new enough; nothing has to be updated for QSV on Alder Lake.

  **vms itself, checked** (2026-10-27, same machine, the installer of ta_install 229935a, no
  `VMS_VIDEO_ENCODER`, 1080p25 HEVC Main): `decoding on d3d11va, encoding on 'h264_qsv (Intel VPL
  mfxExtEncoderROI)'`, 9.5 % of a core per viewed stream against 74 % on
  `video_transcode_processor`. Details in `../ta_install/windows/FINDINGS.md`.

  **Still open:** more than one stream at once, and how many sessions the iGPU takes before one
  fails to open. `transcode_device_test` has not been run on Windows. 192.168.1.100 is an
  i5-750 without Quick Sync; 192.168.1.82 has an Ivy Bridge HD 2500, whose Quick Sync encodes
  H.264 only and only through the legacy Media SDK runtime, if its driver ships one.
- **HEVC on QSV** opened and encoded nothing on that machine on Linux (`qsv: opened but encodes
  nothing`, before this change too). vms encodes H.264 only, so it is not on vms's path.

The other vendors:

- **AMF, for AMD.** `h264_amf` takes D3D11 frames as NVENC does; vcpkg's ffmpeg has an `amf`
  feature. No AMD hardware to check on at all.
- **Media Foundation (`h264_mf`) as a catch-all.** In the Windows FFmpeg already
  (`--enable-mediafoundation`); it reaches whichever vendor's encoder MFT is installed, through
  one API. Considered and left out for now: its latency and rate control on live video are
  unmeasured, and it would need a measurement against NVENC on the NVIDIA box first.

Each one is a backend in roi_transcode and an entry after `IntelOneVpl` in `video_srv`'s default
order (`hw_transcode_processor.cpp`, `encoder_choice_from_env`), and `is_hardware` there learns it.
