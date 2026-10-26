# Hardware encoding on Windows without an NVIDIA card

Found 2026-10-26, while moving NVENC on Windows from CUDA to Direct3D 11 (roi_transcode,
`Engine::Nvenc` and `DecodeEngine::D3d11va`). The decode half of a vendor-neutral Windows path is
there now: D3D11VA, in the Windows FFmpeg already (vcpkg builds it with `--enable-d3d11va`), on
any vendor's GPU. The encode half is not, so on a Windows machine with an Intel or AMD GPU and no
NVIDIA card every HEVC stream still goes to `video_transcode_processor`, decode included — 121–135%
of a core per 1080p25 stream on the i5-750 (`../ta_install/windows/FINDINGS.md`).

What is left:

- **QSV through D3D11, for Intel.** `ffmpeg[qsv]` in the Windows vcpkg ports
  (`scripts/win/Setup-WindowsBuild.ps1`) brings libvpl, and the dispatcher finds Intel's runtime
  in the graphics driver. The library's QSV backend (`src/engine/onevpl/onevpl_engine.cpp`) opens
  its device with `child_device_type=vaapi`, which on Windows has to be `d3d11va`; a QSV device
  derived from the D3D11VA one would also let the decoder's frames reach it without a copy. Needs
  a machine with a recent Intel iGPU to check. 192.168.1.100 is an i5-750 without Quick Sync;
  192.168.1.82 has an Ivy Bridge HD 2500, whose Quick Sync encodes H.264 only and only through the
  legacy Media SDK runtime, if its driver ships one — not checked.
- **AMF, for AMD.** `h264_amf` takes D3D11 frames as NVENC does; vcpkg's ffmpeg has an `amf`
  feature. No AMD hardware to check on at all.
- **Media Foundation (`h264_mf`) as a catch-all.** In the Windows FFmpeg already
  (`--enable-mediafoundation`); it reaches whichever vendor's encoder MFT is installed, through
  one API. Considered and left out for now: its latency and rate control on live video are
  unmeasured, and it would need a measurement against NVENC on the NVIDIA box first.

Each one is a backend in roi_transcode and an entry after `Nvenc` in `video_srv`'s default order
(`hw_transcode_processor.cpp`, `encoder_choice_from_env`), and `is_hardware` there learns it.
