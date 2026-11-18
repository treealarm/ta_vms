# ta-deps: the shared C++ base image

> Moved out of `CLAUDE.md` to keep the always-loaded rules short. This is the full statement;
> the rule itself lives there.

## ta-deps is shared with video_a

`ta-deps` (this repo's `ta-deps/Dockerfile`) is the C++ toolchain + vcpkg base image for **two**
consumers: this repo's `video_srv` (`vms` service) and the separate `video_a` repo's
`analytics-worker`, which builds `FROM ta-deps` via a sibling checkout (see `video_a/scripts/build.sh`).
It installs protobuf/grpc/spdlog/ffmpeg for video_srv plus openvino for video_a — when changing
its package list, consider both consumers.

`video_a`'s CI does not follow this repo: it builds against the `treealarm/ta-deps` tag written in
`video_a/ta-deps.tag`. After publishing a new `ta-deps` (`../ta_install/scripts/push-deps-images.sh`),
bump that file in `video_a`, in a commit of its own — until then video_a keeps building on the old
base, which is safe but means a package added here for video_a is not there yet.

**Adding an ffmpeg feature to the shared vcpkg tree breaks every stale build directory.**
`video_srv` and `video_a` configure against `/mnt/wdblue/vcpkg` in classic mode, and both
link ffmpeg through `pkg_check_modules(FFMPEG ...)` — as does `roi_transcode`, from its own
repository, against the same tree. That call caches its answer in `CMakeCache.txt` and never
asks pkg-config again. So when ffmpeg was once rebuilt there with `qsv,vaapi`,
`libavutil.a` gained
`hwcontext_vaapi.o` while every build directory configured before that kept a link line with
no `-lva`. The result is a wall of `undefined reference to vaGetDisplayDRM` that looks like a
source problem and is not: the `.pc` file is already correct, the cache is not. Delete the
build directory and reconfigure. Docker builds never hit this — they configure from scratch.

**`roi_transcode` does not use it.** It lives in its own repository now (github
`treealarm/roi_transcode`, checked out beside this one) and has its own base, `roi-deps`,
built there by `./docker/build.sh`. The split is load-bearing for `qsv` — the oneVPL
dispatcher is dead weight without `libmfx-gen1.2` in a runtime image — and for openvino,
which is 143 of the 234 minutes ta-deps takes to build and which roi_transcode does not link.

**`vaapi` is the exception, and it is in `ta-deps` deliberately.** video_srv transcodes live
HEVC to H.264 for every WebRTC viewer, one encode per stream, continuously; that is the largest
sustained CPU cost in the service, and `h264_vaapi` measured 43% less CPU per stream on an
i7-12700 iGPU. It brings the system `libdrm-dev` vcpkg's libva demands into `ta-deps` with it.
`video_a`'s analytics-worker pays for this in image size and nothing else.

Building it is not using it. Three things have to line up in the *runtime* image, and each one
failing looks the same from outside — a service that merely burns CPU: an iHD driver new enough
to encode (hence ubuntu:24.04 in the root `Dockerfile`'s runtime stage, not 22.04), vcpkg's own
`libva.so.2` shipped beside the binary (the distribution's older one wins otherwise and the first
hardware encode dies on `undefined symbol: vaMapBuffer2`, lazily, hours after startup), and
`LIBVA_DRIVERS_PATH` pointing at the distribution's `dri` directory (vcpkg's libva searches its
own build prefix, which does not exist in the image). `/dev/dri` passthrough is opt-in per host —
see `../ta_install/docker-compose.gpu.yml.example`.

Both images use an overlay vcpkg triplet
(`<image>/triplets/x64-linux.cmake`) to build release-only (no debug artifacts — vcpkg's
`VCPKG_BUILD_TYPE` env var is a no-op, it only works from inside a triplet file); consumers must
still pass `-DCMAKE_BUILD_TYPE=Release` on their own `cmake` configure step to select the release
libs.
