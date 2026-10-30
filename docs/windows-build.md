# Building on Windows

`scripts/win/Setup-WindowsBuild.ps1` checks the tools a Windows machine needs, offers to
install whatever is missing, and then builds the project. On Linux this role is filled by
`ta-deps/Dockerfile`; there is no Windows equivalent, and several of the steps are not
obvious enough to leave to memory.

```powershell
.\scripts\win\Setup-WindowsBuild.ps1 -CheckOnly   # report only, install nothing
.\scripts\win\Setup-WindowsBuild.ps1              # ask before each install, then build
.\scripts\win\Setup-WindowsBuild.ps1 -Yes         # no prompts (CI)
```

Anything already installed is left alone. Declining an install is not fatal: the run
continues and the summary lists what is still missing.

| Parameter | Purpose |
|---|---|
| `-VcpkgRoot <path>` | vcpkg to use. Otherwise `$env:VCPKG_ROOT`, then the usual locations |
| `-Configuration` | `Debug` (default) or `Release` |
| `-Yes` | Answer every prompt with yes |
| `-CheckOnly` | Report and stop |
| `-SkipBuild` | Install, but do not build |

## Scope

**Build only.** Docker, the Dapr CLI and `dapr init` are needed to *run* the stack, not to
compile it, and the script does not touch them. For running from VS Code see
[the F5 setup](#running) below.

## What it installs

Through `winget`, which ships with Windows 11:

The only thing you have to do by hand is install Git and clone the repository — the script
lives inside it. Everything after that it can handle itself.

| Tool | Why |
|---|---|
| Git | the repository and its two submodules |
| PowerShell 7 | vcpkg builds some ports through it |
| VS Build Tools + C++ workload | MSVC x64, the Windows SDK, and the bundled CMake and Ninja |
| CMake ≥ 3.22, Ninja | only if the VS workload did not provide them |
| .NET SDK 10 | every service targets `net10.0` |
| Node.js 20.19+ | the React client, and `dotnet build` itself — the solution includes `web_srv.client.esproj` |
| vcpkg | cloned and bootstrapped if absent |

vcpkg ports, classic mode (the repository has no `vcpkg.json`):

```
protobuf grpc spdlog pkgconf ffmpeg[gpl,x264,x265,opus,vpx,aom,openssl]
```

`pkgconf` is required, not optional: `video_srv/CMakeLists.txt` calls
`find_package(PkgConfig REQUIRED)` and then `pkg_check_modules(FFMPEG ...)`, which needs a
pkg-config implementation. `openvino` from `ta-deps/Dockerfile` is **not** needed here —
it belongs to the sibling `video_a` repository.

Building these ports from source takes a long time; ffmpeg and grpc dominate. This is the
step worth doing once and keeping.

## Submodules

`roitrc/ta_roienc` is a submodule, public, and `.gitmodules` names it over HTTPS, so a machine
with no SSH key fetches it anonymously. OnvifLib and
KeycloakAdmin are NuGet packages and need no checkout at all.

## Things that are easy to get wrong

Each of these was hit in practice, and the script works around it.

- **`VsDevCmd.bat` overwrites `VCPKG_ROOT`** with the vcpkg bundled inside Visual Studio.
  That copy runs in manifest mode with none of our ports, so the build fails with
  *"Could not locate a manifest (vcpkg.json)"*. The chosen root is saved and restored.
- **`VsDevCmd.bat` shells out to a bare `vswhere.exe`**, which is not on `PATH` by default;
  without help the batch fails with *"'vswhere.exe' is not recognized"*.
- **CMake and Ninja live inside Visual Studio**, not on `PATH`. Probing them before the
  developer environment is loaded reports a false "not found" and installs a second copy.
- **PowerShell does not expand `$var` inside a native-command argument that starts with
  `-D`.** Writing `-DCMAKE_BUILD_TYPE=$Configuration` inline puts the literal text
  `$Configuration` into the CMake cache, and ninja then fails to parse its own rules file.
- **`winget` does not update the current shell's `PATH`**, so a freshly installed tool is
  invisible to the next check until the environment is re-read.
- **A running service locks its assemblies.** Windows refuses to overwrite them, so a build
  started while the stack is up fails with `MSB3021 … locked by .NET Host` instead of
  picking up the change. The script refuses to build and says which processes to stop.
- **`git` blocks on a credential prompt** when a remote needs authentication and none is
  configured. `GIT_TERMINAL_PROMPT=0` turns that into an immediate failure, which is what
  makes the HTTPS fallback above possible and keeps `-Yes` runs from hanging forever.
- **CRLF in files that Linux containers read.** Git for Windows checks out text files with
  CRLF by default (`core.autocrlf=true`), and `scripts/*.sh` is bind-mounted straight into
  the `db-init` container. A CRLF shell script does not fail cleanly: `set -euo pipefail`
  itself is rejected, so none of the shell's error handling is enabled and the script still
  exits 0 with nothing done. `.gitattributes` pins the line endings per extension, so this
  is handled — but a working tree cloned before that was added keeps its CRLF. Fix such a
  checkout once with `git add --renormalize . && git checkout .`.

## What has not been exercised

The checks and the build are verified on a machine that already had everything, and the
unattended `-Yes` path by [CI](#ci). The **install** paths — VS Build Tools, Node, the .NET
SDK, and cloning plus bootstrapping vcpkg — have never actually run: the CI runner has them
all. Treat the first run on a genuinely clean machine as a test of this script rather than a
guarantee. Installing VS Build Tools may also want a reboot.

## CI

`.github/workflows/windows.yml` runs this script on a GitHub-hosted `windows-latest` runner:
once with `-Yes -SkipBuild` for the vcpkg ports, then with `-Yes -Configuration Release` for
the build, followed by `dotnet test`. It runs on every push to `master` that touches the code
it builds, and by hand from the repository's Actions page or with:

```
gh workflow run windows.yml --ref <branch>
```

`vms.exe` is then started once, with only the variables logging needs, and must stop at the
first Dapr variable it requires. That proves every DLL it needs ships beside it: a missing one
ends the process before `main`, silently, with exit code `0xC0000135`.

The result is the asset `ta_vms-windows-x64.zip` of the `windows-build` prerelease for a run on
`master`, and the artifact `ta_vms-windows-x64` for a run on any other branch:

- `vms\` holds `vms.exe` with the vcpkg DLLs it needs.
- `dotnet\<service>\` holds each .NET service, published as its image publishes it. A service is
  a project with a `Dockerfile` beside it. They are framework-dependent, so the machine needs
  the .NET 10 runtime. `web_srv.Server` carries the built web client in `wwwroot`.
- `redist\vc_redist.x64.exe` is the VC++ redistributable from the Visual Studio whose compiler
  built `vms.exe` (as CMake recorded it). The runtime it installs is not among the DLLs beside
  `vms.exe`, and the smoke run cannot catch it missing — the runner has it system-wide.
- `licenses\` holds this repository's licence and, under `vcpkg\`, the copyright file of every
  vcpkg port — the licences of the DLLs beside `vms.exe`, FFmpeg's GPL build among them.

The `windows-build` release is what the Docker-free Windows installer in `../ta_install/windows`
is built from, as its compose stack runs the Docker Hub images. Every push to `master` replaces
its asset, moves its tag to the commit built and names that commit in its notes; a run for an
older commit (a re-run of an old run) leaves it alone. `scripts/win/Publish-ReleaseAsset.ps1`
does this for it and for `windows-deps` alike.

- **The ports are built release-only**, through the overlay triplet
  `scripts/win/triplets/x64-windows.cmake`. That halves their build time and their cache, and
  is why CI builds Release: a Debug `vms.exe` cannot link against release-only libraries
  (LNK2038). Developer machines keep vcpkg's own triplet, which has both.
- **The built ports are kept as an asset of the `windows-deps` prerelease**, as vcpkg's binary
  cache in one tar. A cold run builds every port from source and takes about two hours; with the
  asset in place a run takes minutes for the ports. Each run replaces the asset with exactly the
  ports it used. Delete the asset to force a full rebuild.
- **vcpkg is pinned** by `VCPKG_COMMIT` in the workflow, the same commit as the encoder's
  `builtin-baseline`. Moving it changes the ABI hash of most ports, so the next run is cold
  again.

## Output

- `video_srv\build\vms.exe` — the C++ media server, built with MSVC. No source changes
  are needed for Windows; the same sources compile as on Linux.
- `dotnet\**\bin\Debug\net10.0\` — the .NET services.

`cmake --preset windows-default` is used, and that preset reads `$env{VCPKG_ROOT}` — so
nothing in the repository has to be edited for a particular machine.
`video_srv/CMakeUserPresets.json` is a personal file (it is in `.gitignore`) and is only
there for IDE convenience.

## Running

Building is not enough to start the stack: that also needs Docker (PostgreSQL, MediaMTX,
Dapr placement, and RabbitMQ for the Dapr bus) and the Dapr CLI with
`dapr init`. From VS Code, use the **Full stack (F5) [Windows]** compound.

`docker compose up` also runs `db-init`, which creates the service databases — including the
one behind the Dapr state store — and then exits. The services debugged on the host connect to
`localhost:5432`, and so does the state store component (`init_files/dapr_components/statestore.yaml`),
so make sure the published port belongs to *this* stack's PostgreSQL: `multitenant_admin`
publishes 5432 as well, and whichever container started first owns it. `docker compose run --rm
db-init --check` reports which server it actually reached and what is missing.
