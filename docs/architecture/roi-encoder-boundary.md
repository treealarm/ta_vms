# The boundary between `roitrc/` and `roitrc/ta_roienc/`

> Moved out of `CLAUDE.md`. This is the full statement, including the Windows engines and the
> vaapi rules; the rule itself lives there.

## The boundary between `roitrc/` and `roitrc/ta_roienc/`

`roitrc/ta_roienc` is a submodule on github `treealarm/roi_transcode`, the `ta_roienc` library
(adaptive ROI encoding). That repository holds only the library's code and its C++ unit tests; it
deliberately knows nothing of gRPC, Dapr or protobuf. It may be edited — it is our library — but
everything that makes sense only for ta_vms lives here, not there.

**It has three consumers, and the second raises the cost of a mistake.** Besides `roitrc`, it is
linked by `video_srv` — live HEVC→H.264 transcoding for WebRTC goes through
`ta_roienc::Transcoder`, because libavcodec has no automatic choice of hardware and
`avcodec_find_encoder` always returns libx264 (see
`video_srv/writer/processors/hw_transcode_processor.cpp`).
**The default engine is hardware only — `Engine::Auto` restricted to VA-API, NVENC, then QSV — not
the library's whole `Engine::Auto`**, and the difference is one of meaning: the library is linked
only for the hardware. Whichever of them the build or the machine lacks declines at once ("not
in this FFmpeg build", "no CUDA device"), so one order serves both platforms. If neither opened — or
an `auto` chain set explicitly through `VMS_VIDEO_ENCODER` came down to a software one — the stream
is handed back to `compat_sink`, and `video_transcode_processor` takes it. That one has
`tune=zerolatency`, and the library's software engine does not: 34 frames in the pipeline against
4 through `h264_vaapi`, which is a second and a half of delay on live video.
**On Windows the hardware engines are NVENC and QSV**: the Windows FFmpeg has nvcodec and qsv and
no `h264_vaapi`. On the i5-750 test machine with a GTX 1050 Ti a 1080p25 HEVC stream costs vms 7%
of a core through NVDEC→NVENC against 121–135% through `video_transcode_processor`
(`../ta_install/windows/FINDINGS.md`). A GeForce opens at most 8 encode sessions; the ninth
stream fails to open ("incompatible client key") and takes the CPU path. NVENC is reached through
libavcodec, which reads no regions, so it holds the bitrate target with its own VBR rather than the
library's outer loop — that loop moves a background QP offset carried as a region, and on NVENC
it moved nothing (10 Mbit/s against a 3.9 Mbit/s target). There NVENC runs on a Direct3D 11
device rather than CUDA — the library asks whether the FFmpeg has D3D11VA, which only the Windows
one does — on the NVIDIA adapter by vendor id, and the decoder it hands that device to is
D3D11VA, the decode every Windows GPU has. QSV runs the same way on the Intel adapter: a QSV
device derived from a D3D11 one, the decoder handed the D3D11 device, and each decoded texture
mapped into QSV's pool rather than copied (on Linux the same code runs on VA-API instead of D3D11).
That pool has to be a dynamic one — a fixed pool refuses surfaces it did not allocate — and the
mapping goes through a pool derived from the decoder's, the only kind QSV can unmap. NVENC comes
first, so a machine with both uses the card. vms decodes on the encoder's device or not at all, so
a Windows machine with neither — AMD — has no hardware engine, and the stream goes to
`video_transcode_processor`, decode included (`docs/future_plans/10-windows-hardware-encoders.md`).
Do not build Windows `vms.exe` without the library — the engines arrive through it.
The third is the separate `video_a` repository, which has its own `ta_roienc` submodule and links
only the decoder (`ta_roienc_decode`) for the analytics worker; a rename in the library's public
API has to be carried there too, and its submodule bumped in a commit of its own.

**A build without the submodule must keep working** — a clone made without `--recursive` has an
empty `roitrc/ta_roienc`, and that is a reason to warn, not to fail.
`video_srv/CMakeLists.txt` adds it only when `roitrc/ta_roienc` is checked out; otherwise it
warns, leaves `hw_transcode_processor` out (`VMS_WITH_TA_ROIENC` is undefined) and every HEVC
stream takes `video_transcode_processor`. Such a binary refuses to start with `VMS_VIDEO_ENCODER`
set, since it cannot honour it. `roitrc/CMakeLists.txt` warns and builds an empty `roitrc` target.
The image build passes `-DVMS_REQUIRE_TA_ROIENC=ON`, so an image can never lose the hardware path
silently — keep it that way. New code that touches `ta_roienc` from `video_srv` goes behind the
same switch.
A broken submodule bump takes live video down, not only the clip queue. Bump the version in a
commit of its own and check both paths.

`roitrc/` is everything about re-compression that is ours: the gRPC contract `VmsFs` calls, the job
model (it mirrors EdgeImport in `video_srv`), the Dapr notifications and the `ROI_ALLOWED_ROOTS`
path sandbox. The dependency arrow is one and narrow: only the public API is linked —
`ta_roienc/ta_roienc.h`, `ta_roienc/transcoder.h` and the headers they include — and nothing reaches into the
library's internals. If new code needs the encoder's `src/`, that is a sign it is on the wrong side
of the boundary.

The contract `proto/roi_transcode.proto` lives here, beside the others, in a single copy.

It is built by `roitrc/CMakeLists.txt` through `add_subdirectory(ta_roienc)` with
`TA_ROIENC_BUILD_TESTS` and `TA_ROIENC_INSTALL` off: the library's unit tests run in its own
repository. The service's own checks are `roitrc/scripts/check.sh` (startup with a broken
configuration, both gRPC entry points, the sandbox). The `roi-deps` base image is built from the
submodule's `docker/intel/`; `roitrc/Dockerfile` takes the repository root as its context, because
it needs both `roitrc/` and `proto/`.

Updating the library's version is an ordinary submodule bump, in a commit of its own, so that the
history shows which encoder went into a release.
