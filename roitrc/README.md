# roitrc — ROI re-compression service

ta_vms' service around the **ta_roienc** encoder library, which lives in its own repository
and is pinned here as the `ta_roienc` submodule; everything else in this directory is the
service.

```
roitrc/
  ta_roienc/  submodule -> github treealarm/roi_transcode (the library)
  svc/        the service: gRPC entry points, jobs, Dapr, the path sandbox
  tools/      roi_grpc_smoke -- a client, for driving the service without VmsFs
  tests/      path_guard_test
  testdata/   synth_roi.json, the fixture check.sh drives the smoke client with
  scripts/    check.sh
```

The dependency arrow points one way and is narrow: this links `ta_roienc` through its public
headers alone — `ta_roienc/ta_roienc.h`, `ta_roienc/session.h`, `ta_roienc/types.h`. Nothing
here reaches into the library's `src/`, and nothing of the service belongs on the other side
of that line.

## Build

```bash
git submodule update --init            # once
export VCPKG_ROOT=/path/to/vcpkg
cmake --preset linux-dev
cmake --build build -j
./scripts/check.sh
```

`check.sh` covers what this service has to get right: startup refusing a broken
configuration, both gRPC entry points, the path sandbox, and the unit tests. The library's
own unit tests run in its repository.

## Run

```bash
APP_ID=roitrc GRPC_ROI_BIND=127.0.0.1 GRPC_ROI_PORT=50061 \
  ROI_ALLOWED_ROOTS="$PWD:/tmp" ./build/roi-transcode-svc
```

| Env | Required | Meaning |
|---|---|---|
| `APP_ID` | yes | service id in logs / events |
| `GRPC_ROI_BIND` | yes | listen address |
| `GRPC_ROI_PORT` | yes | listen port |
| `ROI_ALLOWED_ROOTS` | yes | `:`-separated absolute dirs gRPC paths must sit under |
| `DAPR_GRPC_PORT` | no | with `PUBSUB_NAME`, publish job changes |
| `PUBSUB_NAME` | no | Dapr pubsub component name |

All four required variables are checked at startup, `ROI_ALLOWED_ROOTS` included: a
misconfigured sandbox fails the process instead of the first request.

Topic: `roi-transcode-job-changed`. Without the Dapr variables, publishing is a no-op.

## Two entry points, one `Settings`

- **File job** — `StartFileJob` / `GetJob` / `CancelJob` / `ListJobs`, the same pattern as
  `media_server`'s EdgeImport: start returns immediately, poll for the result, cancel is
  best effort.
- **Frame stream** — `EncodeStream`, bidirectional, for a caller with no file. First
  message is `open`, then frames carrying a mask or boxes. With `output_path` the packets
  are muxed there; otherwise they come back over the stream.

Contract: [`../proto/roi_transcode.proto`](../proto/roi_transcode.proto), beside every
other ta_vms service contract. `VmsFs` generates the C# client from the same file.

```bash
./build/roi_grpc_smoke 127.0.0.1:50061 clip.mp4 out.mp4 roi.json
./build/roi_grpc_smoke 127.0.0.1:50061 clip.mp4 out.mp4 --stream
```

Both paths given to a caller must sit under `ROI_ALLOWED_ROOTS`: caller and service share a
filesystem, so the sandbox is enforced on this side.

## Which node talks to which instance

Sharded by node, and that is correctness rather than capacity: this service is handed
absolute paths to clips on its own node's disk, so an instance elsewhere either finds
nothing there or finds a *different* node's file at the same path. A node names its
instance in the fifth field of `MEDIA_SERVER_PAIRS`, resolved through VmsCfg. A node that
names none simply does not re-compress.

## Updating the encoder

```bash
git -C ta_roienc fetch && git -C ta_roienc checkout <commit>
git add ta_roienc && git commit
```

Its own repository is where the library is developed and tested; bumping the pin is a
separate commit so the history says which encoder went into a release.
