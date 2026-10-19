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

`SquareIntegrationClient` and `roitrc/ta_roienc` are submodules, both public, and `.gitmodules`
names them over HTTPS, so a machine with no SSH key fetches them anonymously. OnvifLib and
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

The checks and the build are verified on a machine that already had everything. The
**install** paths — VS Build Tools, Node, the .NET SDK, and cloning plus bootstrapping
vcpkg — have never actually run, so treat the first run on a genuinely clean machine as a
test of this script rather than a guarantee. Installing VS Build Tools may also want a
reboot.

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
