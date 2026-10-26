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
  differs — D3D11 on the adapter with vendor id `0x8086` — and that has run nowhere yet. The
  i7-12700 under Windows is the machine for it (the user, 2026-10-26): `transcode_device_test`
  there, then vms with an HEVC camera and no `VMS_VIDEO_ENCODER`, whose log names the engine
  (`backend: qsv ... opened`) and whose CPU per stream should be in NVENC's range, not the
  software path's. 192.168.1.100 is an
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
