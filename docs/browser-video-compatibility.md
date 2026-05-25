# Browser video compatibility for HLS archive playback

The archive is stored as MPEG-TS (`.ts`) segments containing either H.264 or HEVC video.
Playback uses hls.js v1.x, which feeds segments to the browser via MSE (Media Source Extensions).
Different browsers and OS configurations have different constraints.

---

## H.264 archive

Works everywhere **provided the H.264 profile is standard** (Baseline / Main / High, profile_idc ≤ 100).

### Problem: High 4:4:4 Predictive profile (profile_idc = 0xF4 = 244)

`ffmpeg -c:v libx264 -preset ultrafast -tune zerolatency` without an explicit pixel format may
encode in **High 4:4:4 Predictive** profile if the input colorspace is not `yuv420p` (e.g. `testsrc`
in newer FFmpeg versions can produce `rgb24` or `yuv444p`). Browsers do not support this profile
via MSE — hls.js throws `bufferAddCodecError`.

**Symptoms:**
```
Cannot play media. No decoders for requested formats: video/mp4;codecs=avc1.f4001f
```

**Diagnosis:**
```bash
ffprobe -v error -select_streams v:0 \
  -show_entries stream=codec_name,profile \
  -of default=noprint_wrappers=1 file.ts
# profile=High 4:4:4 Predictive  ← bad
# profile=High                    ← ok
```

**Fix:** add `-pix_fmt yuv420p` to the FFmpeg encode command:
```ini
ExecStart=/usr/bin/ffmpeg -re -f lavfi -i testsrc=size=1280x720:rate=30 \
  -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p \
  -f rtsp rtsp://127.0.0.1:8554/cam1
```

---

## HEVC archive

HEVC in MPEG-TS is **never** supported via MSE (`MediaSource.isTypeSupported('video/mp2t; codecs="hvc1..."')` = `false` everywhere).
hls.js v1.x handles this by demuxing the TS in JavaScript and remuxing to fMP4 before feeding MSE.
This requires the browser to support HEVC in fMP4: `MediaSource.isTypeSupported('video/mp4; codecs="hvc1..."')`.

### Firefox

Works if `libavcodec-extra` is installed (provides FFmpeg with HEVC support used by Firefox).

```bash
sudo apt install libavcodec-extra ubuntu-restricted-extras
```

### Chrome on Linux

Requires hardware HEVC decoding via VA-API **and** libva ≥ 2.17 (VA-API ≥ 1.17).

| Ubuntu version | libva version | Chrome HEVC via MSE |
|----------------|---------------|---------------------|
| 22.04 LTS      | 2.14 (1.14)   | No — too old        |
| 24.04 LTS      | 2.20+ (1.20+) | Yes (with GPU)      |

**Diagnosis — check VA-API version:**
```bash
vainfo 2>/dev/null | head -1
# vainfo: VA-API version: 1.14 ← too old for Chrome HEVC
```

**Diagnosis — check Chrome:**
```bash
# In Chrome DevTools console:
MediaSource.isTypeSupported('video/mp4; codecs="hvc1.1.6.L93.B0"')
# true  → HEVC archive will work
# false → Chrome cannot decode HEVC via MSE on this system
```

**Error when libva is too old:**
```
[ERROR:media/gpu/vaapi/vaapi_wrapper.cc] Installed VAAPI version is too old.
min supported version: 1.17 installed version: 1.14
```

**Fix:** upgrade to Ubuntu 24.04, which ships libva 2.20+ out of the box.

### Chrome — required launch flags (Ubuntu 24.04)

After upgrading the OS, Chrome uses VA-API for HEVC automatically. Optionally:
```bash
google-chrome --enable-features=PlatformHEVCDecoderSupport,VaapiVideoDecodeLinuxGL
```

---

## Summary table

| Browser          | OS / config              | H.264 archive | HEVC archive |
|------------------|--------------------------|---------------|--------------|
| Firefox          | Ubuntu + libavcodec-extra | Yes           | Yes          |
| Firefox          | Ubuntu, no extras         | Yes           | No           |
| Chrome           | Ubuntu 24.04, GPU w/ HEVC | Yes           | Yes          |
| Chrome           | Ubuntu 22.04              | Yes           | No (libva too old) |
| Chrome           | macOS / Windows 11        | Yes           | Yes (hw decoder) |
