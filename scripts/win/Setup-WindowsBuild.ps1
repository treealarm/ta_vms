#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the tools needed to build ta_vms on Windows, offers to install the missing
    ones, and then builds the project.

.DESCRIPTION
    On Linux this role is filled by ta-deps/Dockerfile. There is no Windows equivalent,
    so a clean machine needs a fair amount of manual setup — and several steps are not
    obvious. This script encodes them.

    Scope is the build only. Docker, the Dapr CLI and `dapr init` are needed to *run* the
    stack, not to compile it, and are deliberately left alone.

    Nothing already present is touched: every tool is probed first, and only what is
    missing is offered for installation, one prompt at a time.

.PARAMETER VcpkgRoot
    vcpkg installation to use. Resolution order: this parameter, then $env:VCPKG_ROOT,
    then the usual locations.

.PARAMETER Yes
    Answer every prompt with yes. For unattended runs.

.PARAMETER CheckOnly
    Report what is missing and stop. Installs nothing, builds nothing.

.PARAMETER SkipBuild
    Install what is missing, but do not build afterwards.

.EXAMPLE
    .\Setup-WindowsBuild.ps1 -CheckOnly
    .\Setup-WindowsBuild.ps1
    .\Setup-WindowsBuild.ps1 -VcpkgRoot D:\vcpkg -Yes
#>
[CmdletBinding()]
param(
    [string]$VcpkgRoot = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Triplet = "x64-windows",
    [switch]$Yes,
    [switch]$CheckOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$VideoSrvDir = Join-Path $RepoRoot "video_srv"
$Solution = Join-Path $RepoRoot "dotnet\ta_vms.sln"

# vcpkg ports for video_srv, in classic mode (the repo has no vcpkg.json).
# pkgconf is not optional: CMakeLists.txt calls find_package(PkgConfig REQUIRED) and then
# pkg_check_modules(FFMPEG ...), which needs a pkg-config implementation on PATH — vcpkg
# supplies one. openvino from ta-deps/Dockerfile is NOT needed here; it belongs to the
# sibling video_a repository (see CLAUDE.md).
$VcpkgPorts = @(
    'protobuf',
    'grpc',
    'spdlog',
    'pkgconf',
    'ffmpeg[gpl,x264,x265,opus,vpx,aom,openssl]'
)

# Missing prerequisites collected as we go, reported together at the end.
$script:Missing = @()
$script:Skipped = @()

# Stage numbering, so a long run shows how far along it is. Ten checks, then two builds;
# every stage prints its header even when it ends up skipping, so the count stays honest.
$script:StageNo = 0
$script:StageTotal = if ($CheckOnly -or $SkipBuild) { 10 } else { 12 }
$script:StageWatch = $null
$script:TotalWatch = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------- output helpers

function Write-Head($text) {
    # Report how long the previous stage took before opening the next one.
    if ($script:StageWatch) {
        $elapsed = $script:StageWatch.Elapsed
        $shown = if ($elapsed.TotalSeconds -ge 1) { "{0:n1}s" -f $elapsed.TotalSeconds } else { "<1s" }
        Write-Host "         (done in $shown)" -ForegroundColor DarkGray
    }
    $script:StageNo++
    $script:StageWatch = [System.Diagnostics.Stopwatch]::StartNew()

    Write-Host ""
    Write-Host ("== [{0}/{1}] {2}" -f $script:StageNo, $script:StageTotal, $text) -ForegroundColor Cyan
}
function Write-Ok($text) { Write-Host "  [ ok ] $text" -ForegroundColor Green }
function Write-Miss($text) { Write-Host "  [ -- ] $text" -ForegroundColor Yellow }
function Write-Bad($text) { Write-Host "  [fail] $text" -ForegroundColor Red }
function Write-Note($text) { Write-Host "         $text" -ForegroundColor DarkGray }
function Write-Step($text) { Write-Host "  ...   $text" -ForegroundColor DarkCyan }

function Write-Section($text) {
    # Unnumbered header, for the summary — it is not a stage of the work.
    if ($script:StageWatch) {
        $elapsed = $script:StageWatch.Elapsed
        $shown = if ($elapsed.TotalSeconds -ge 1) { "{0:n1}s" -f $elapsed.TotalSeconds } else { "<1s" }
        Write-Host "         (done in $shown)" -ForegroundColor DarkGray
        $script:StageWatch = $null
    }
    Write-Host ""
    Write-Host "== $text" -ForegroundColor White
}

function Invoke-Native {
    <#
        Runs a native command and returns its exit code.

        Windows PowerShell turns any stderr output from a native command into a
        NativeCommandError, which $ErrorActionPreference='Stop' promotes to a terminating
        error. winget, git and vcpkg all write ordinary progress to stderr, so a plain
        call would abort the script on a perfectly successful command. Only the exit code
        says whether anything actually failed.

        Output goes to the host, never down the pipeline: returning it together with the
        exit code would make the caller's variable an array.
    #>
    param([Parameter(Mandatory)][scriptblock]$Script, [switch]$Quiet)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & $Script 2>&1 | Out-Null }
        else { & $Script 2>&1 | ForEach-Object { Write-Host $_ } }
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Update-SessionPath {
    <#
        Re-reads PATH from the registry into this process.

        A tool installed by winget lands in the machine or user PATH, but the environment
        of an already-running shell is a copy made at launch. Without this, the very next
        check would not find what was just installed.
    #>
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:PATH = ($machine, $user | Where-Object { $_ }) -join ';'
}

function Confirm-Action {
    <# Returns $true when the step should run. -Yes answers everything, -CheckOnly nothing. #>
    param([Parameter(Mandatory)][string]$Prompt)

    if ($CheckOnly) { return $false }
    if ($Yes) {
        Write-Note "$Prompt -> yes (-Yes)"
        return $true
    }

    while ($true) {
        $answer = Read-Host "  $Prompt [y/N]"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $false }
        switch ($answer.Trim().ToLowerInvariant()) {
            'y' { return $true }
            'yes' { return $true }
            'n' { return $false }
            'no' { return $false }
            default { Write-Host "  Please answer y or n." -ForegroundColor DarkGray }
        }
    }
}

function Install-WithWinget {
    <# Installs a package and refreshes PATH so later checks can see it. #>
    param(
        [Parameter(Mandatory)][string]$Id,
        [string]$Name = "",
        [string[]]$ExtraArgs = @()
    )
    if (-not $Name) { $Name = $Id }

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Bad "winget is not available; install $Name manually."
        return $false
    }

    Write-Host "  installing $Name ..." -ForegroundColor DarkGray
    $wingetArgs = @(
        'install', '--id', $Id, '--exact',
        '--accept-package-agreements', '--accept-source-agreements',
        '--disable-interactivity'
    ) + $ExtraArgs

    $code = Invoke-Native { & winget @wingetArgs }
    # winget reports "no applicable upgrade"/"already installed" with non-zero codes that
    # are not failures; treat only a genuinely absent tool as one, which the re-check below
    # decides anyway.
    Update-SessionPath
    if ($code -ne 0) {
        Write-Note "winget exited with $code — verifying whether the tool is usable anyway"
    }
    return $true
}

# ------------------------------------------------------------------ Visual Studio / MSVC

function Get-VsWherePath {
    <# vswhere ships with every VS installer since 2017 and lives at a fixed location. #>
    foreach ($candidate in @(
            (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"),
            (Join-Path $env:ProgramFiles "Microsoft Visual Studio\Installer\vswhere.exe"))) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return $null
}

function Get-VisualStudio {
    <#
        Any edition with the x64 C++ toolset will do: Build Tools, Community, Professional,
        release or preview. Nothing here pins a version, so a VS upgrade does not break the
        script.
    #>
    $vswhere = Get-VsWherePath
    if (-not $vswhere) { return $null }

    $json = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -format json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }

    $installs = $json | ConvertFrom-Json
    if (-not $installs -or @($installs).Count -eq 0) { return $null }
    return @($installs)[0]
}

function Enter-MsvcEnvironment {
    <#
        Imports the MSVC x64 developer environment into this session: cl.exe, the Windows
        SDK, and the cmake/ninja that ship with Visual Studio.

        Two traps are handled here, both observed on a working machine:

        1. VsDevCmd.bat shells out to a bare `vswhere.exe`, which only resolves if the VS
           Installer directory is on PATH — it is not by default, and the batch then fails
           with "'vswhere.exe' is not recognized".

        2. VsDevCmd.bat exports its own VCPKG_ROOT, pointing at the vcpkg bundled with
           Visual Studio. That copy runs in manifest mode with none of our ports installed,
           so letting it overwrite a deliberately chosen VCPKG_ROOT breaks the build with
           "Could not locate a manifest (vcpkg.json)". The caller's value is restored.
    #>
    param([string]$Architecture = "amd64")

    if ($env:VMS_MSVC_ENV_LOADED -eq $Architecture) { return }

    $vs = Get-VisualStudio
    if (-not $vs) { throw "Visual Studio with the C++ toolset was not found." }

    $vsDevCmd = Join-Path $vs.installationPath "Common7\Tools\VsDevCmd.bat"
    if (-not (Test-Path $vsDevCmd)) { throw "VsDevCmd.bat not found under $($vs.installationPath)" }

    $callerVcpkgRoot = $env:VCPKG_ROOT

    $vswhere = Get-VsWherePath
    $savedPath = $env:PATH
    if ($vswhere) {
        $installerDir = Split-Path $vswhere -Parent
        if (($env:PATH -split ';') -notcontains $installerDir) {
            $env:PATH = "$installerDir;$env:PATH"
        }
    }

    try {
        $output = & cmd.exe /c "`"$vsDevCmd`" -arch=$Architecture -host_arch=amd64 -no_logo && set" 2>&1
        $exit = $LASTEXITCODE
    } finally {
        $env:PATH = $savedPath
    }
    if ($exit -ne 0) { throw "VsDevCmd.bat failed:`n$($output -join "`n")" }

    foreach ($line in $output) {
        if ($line -is [string] -and $line -match '^([^=]+)=(.*)$') {
            Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] -ErrorAction SilentlyContinue
        }
    }

    if ($callerVcpkgRoot) { $env:VCPKG_ROOT = $callerVcpkgRoot }
    $env:VMS_MSVC_ENV_LOADED = $Architecture
}

# ------------------------------------------------------------------------------- vcpkg

function Resolve-VcpkgRoot {
    <#
        Prefers a root that actually has classic-mode ports for the triplet. The vcpkg
        bundled with Visual Studio is manifest-only and installs nothing, so it must not
        win merely by being first on PATH or in VCPKG_ROOT.
    #>
    param([string]$Preferred = "")

    $candidates = @()
    if ($Preferred) { $candidates += $Preferred }
    if ($env:VCPKG_ROOT) { $candidates += $env:VCPKG_ROOT }
    $candidates += @("M:\vcpkg", "C:\vcpkg", "C:\dev\vcpkg", (Join-Path $env:USERPROFILE "vcpkg"))
    $onPath = Get-Command vcpkg -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += (Split-Path $onPath.Source -Parent) }

    $usable = @()
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "vcpkg.exe"))) { $usable += (Resolve-Path $c).Path }
    }
    if ($usable.Count -eq 0) { return $null }

    foreach ($u in $usable) {
        if (Test-Path (Join-Path $u "installed\$Triplet\lib")) { return $u }
    }
    return $usable[0]
}

function Get-MissingPorts {
    param([Parameter(Mandatory)][string]$Root)

    $listing = & (Join-Path $Root "vcpkg.exe") list 2>$null
    $missing = @()
    foreach ($port in $VcpkgPorts) {
        # Compare on the base name: `vcpkg list` prints features as separate rows
        # (ffmpeg[x264]:x64-windows) alongside the package itself.
        $base = ($port -split '\[')[0]
        $found = $listing | Where-Object {
            $_ -match "^$([regex]::Escape($base))(\[[^\]]*\])?:$([regex]::Escape($Triplet))\s"
        }
        if (-not $found) { $missing += $port }
    }
    return $missing
}

function Use-HttpsSubmoduleUrls {
    <#
        Points the submodules at https:// instead of git@github.com.

        .gitmodules declares SSH remotes, which need a key with access to the
        organisation. Both submodules are public, so anonymous HTTPS fetches them just as
        well — and a fresh machine has no SSH key at all.

        The override is written to .git/config, not to .gitmodules: the repository keeps
        its SSH remotes for whoever has keys, and nothing shows up as a local change.
    #>
    param([Parameter(Mandatory)][string]$Repo)

    $rewritten = 0
    $entries = & git -C $Repo config --file .gitmodules --get-regexp '^submodule\..*\.url$' 2>$null
    foreach ($entry in $entries) {
        if ($entry -notmatch '^(\S+)\s+(.+)$') { continue }
        $key = $Matches[1]
        $url = $Matches[2].Trim()
        # git@host:owner/repo.git -> https://host/owner/repo.git
        if ($url -match '^git@([^:]+):(.+)$') {
            $https = "https://$($Matches[1])/$($Matches[2])"
            & git -C $Repo config $key $https 2>&1 | Out-Null
            Write-Note "$key -> $https"
            $rewritten++
        }
    }
    if ($rewritten -gt 0) { & git -C $Repo submodule sync 2>&1 | Out-Null }
    return $rewritten
}

# ================================================================================= checks

Write-Host "ta_vms — Windows build prerequisites" -ForegroundColor White
Write-Host "repository: $RepoRoot" -ForegroundColor DarkGray

# ---- git ---------------------------------------------------------------------------
Write-Head "Git"
if (Get-Command git -ErrorAction SilentlyContinue) {
    Write-Ok ((& git --version) -join '')
} else {
    Write-Miss "git not found"
    if (Confirm-Action "Install Git?") {
        Install-WithWinget -Id 'Git.Git' -Name 'Git' | Out-Null
    } else { $script:Skipped += 'Git' }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $script:Missing += 'Git' }
}

# ---- submodules --------------------------------------------------------------------
# KeycloakAdmin and SquareIntegrationClient are submodules; without them the solution does
# not restore. .gitmodules uses SSH remotes, but both repositories are public, so a machine
# with no SSH key can still fetch them over HTTPS — see the fallback below.
Write-Head "Git submodules"
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Miss "skipped — git is not available"
} else {
    Push-Location $RepoRoot
    try {
        $status = & git submodule status 2>$null
        $uninit = @($status | Where-Object { $_ -match '^\-' })
        if (@($status).Count -eq 0) {
            Write-Ok "none declared"
        } elseif ($uninit.Count -gt 0) {
            Write-Miss "$($uninit.Count) submodule(s) not initialised"
            foreach ($u in $uninit) { Write-Note $u.Trim() }
            if (Confirm-Action "Initialise submodules?") {
                Write-Step "fetching submodules"
                # GIT_TERMINAL_PROMPT=0 so a missing SSH key fails immediately instead of
                # blocking on a credential prompt in a non-interactive run.
                $previousPrompt = $env:GIT_TERMINAL_PROMPT
                $env:GIT_TERMINAL_PROMPT = '0'
                try {
                    $code = Invoke-Native { & git submodule update --init --recursive }
                    if ($code -ne 0) {
                        Write-Miss "SSH fetch failed — retrying over HTTPS (both submodules are public)"
                        if ((Use-HttpsSubmoduleUrls -Repo $RepoRoot) -gt 0) {
                            Write-Step "fetching submodules over HTTPS"
                            $code = Invoke-Native { & git submodule update --init --recursive }
                        }
                    }
                } finally {
                    $env:GIT_TERMINAL_PROMPT = $previousPrompt
                }
                if ($code -ne 0) { $script:Missing += 'git submodules' }
                else { Write-Ok "submodules initialised" }
            } else { $script:Skipped += 'git submodules' }
        } else {
            Write-Ok "$(@($status).Count) initialised"
        }
    } finally { Pop-Location }
}

# ---- PowerShell 7 ------------------------------------------------------------------
# vcpkg drives some ports through pwsh; the build cache records Z_VCPKG_PWSH_PATH.
Write-Head "PowerShell 7"
$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if (-not $pwsh -and (Test-Path "$env:ProgramFiles\PowerShell\7\pwsh.exe")) {
    $pwsh = [PSCustomObject]@{ Source = "$env:ProgramFiles\PowerShell\7\pwsh.exe" }
}
if ($pwsh) {
    Write-Ok $pwsh.Source
} else {
    Write-Miss "pwsh not found (vcpkg uses it to build some ports)"
    if (Confirm-Action "Install PowerShell 7?") {
        Install-WithWinget -Id 'Microsoft.PowerShell' -Name 'PowerShell 7' | Out-Null
    } else { $script:Skipped += 'PowerShell 7' }
}

# ---- Visual Studio C++ toolset -----------------------------------------------------
Write-Head "MSVC toolchain"
$vs = Get-VisualStudio
if ($vs) {
    Write-Ok "$($vs.displayName) $($vs.installationVersion)"
    Write-Note $vs.installationPath
} else {
    Write-Miss "no Visual Studio with the x64 C++ toolset"
    Write-Note "Installing Build Tools (compiler, CMake and Ninja; no IDE)."
    if (Confirm-Action "Install Visual Studio Build Tools with the C++ workload?") {
        # --override replaces winget's default arguments, so the workload must be named
        # explicitly or the install lands without a compiler.
        Install-WithWinget -Id 'Microsoft.VisualStudio.2022.BuildTools' -Name 'VS Build Tools' -ExtraArgs @(
            '--override',
            '--quiet --wait --norestart --nocache --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'
        ) | Out-Null
        $vs = Get-VisualStudio
    } else { $script:Skipped += 'Visual Studio Build Tools' }
    if (-not $vs) { $script:Missing += 'MSVC toolchain' }
}

# Everything below wants the developer environment: cmake and ninja ship inside Visual
# Studio and are not on PATH otherwise, so probing them before this point reports a false
# "not found" and installs a redundant copy.
if ($vs) {
    try {
        Enter-MsvcEnvironment
        Write-Ok "developer environment loaded (x64)"
    } catch {
        Write-Bad $_.Exception.Message
        $script:Missing += 'MSVC developer environment'
    }
}

# ---- CMake -------------------------------------------------------------------------
Write-Head "CMake"
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmake) {
    $version = (((& cmake --version) | Select-Object -First 1) -replace 'cmake version ', '').Trim()
    if ([version]($version -replace '-.*$', '') -ge [version]'3.22') {
        Write-Ok "$version ($($cmake.Source))"
    } else {
        Write-Miss "$version is older than the required 3.22"
        if (Confirm-Action "Install a current CMake?") {
            Install-WithWinget -Id 'Kitware.CMake' -Name 'CMake' | Out-Null
        } else { $script:Skipped += 'CMake' }
    }
} else {
    Write-Miss "cmake not found"
    if (Confirm-Action "Install CMake?") {
        Install-WithWinget -Id 'Kitware.CMake' -Name 'CMake' | Out-Null
    } else { $script:Skipped += 'CMake' }
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { $script:Missing += 'CMake' }
}

# ---- Ninja -------------------------------------------------------------------------
Write-Head "Ninja"
$ninja = Get-Command ninja -ErrorAction SilentlyContinue
if ($ninja) {
    Write-Ok "$((& ninja --version)) ($($ninja.Source))"
} else {
    Write-Miss "ninja not found"
    Write-Note "It normally comes with the VS C++ workload; installing a standalone copy."
    if (Confirm-Action "Install Ninja?") {
        Install-WithWinget -Id 'Ninja-build.Ninja' -Name 'Ninja' | Out-Null
    } else { $script:Skipped += 'Ninja' }
    if (-not (Get-Command ninja -ErrorAction SilentlyContinue)) { $script:Missing += 'Ninja' }
}

# ---- .NET SDK ----------------------------------------------------------------------
Write-Head ".NET SDK 10"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$hasNet10 = $false
if ($dotnet) {
    $sdks = & dotnet --list-sdks 2>$null
    $hasNet10 = [bool]($sdks | Where-Object { $_ -match '^10\.' })
}
if ($hasNet10) {
    Write-Ok "$((& dotnet --version).Trim())"
} else {
    Write-Miss $(if ($dotnet) { "no 10.x SDK installed" } else { "dotnet not found" })
    if (Confirm-Action "Install the .NET 10 SDK?") {
        Install-WithWinget -Id 'Microsoft.DotNet.SDK.10' -Name '.NET 10 SDK' | Out-Null
    } else { $script:Skipped += '.NET 10 SDK' }
    $sdks = if (Get-Command dotnet -ErrorAction SilentlyContinue) { & dotnet --list-sdks 2>$null } else { @() }
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) { $script:Missing += '.NET 10 SDK' }
}

# ---- Node.js -----------------------------------------------------------------------
# Needed even for `dotnet build`: the solution includes web_srv.client.esproj, whose
# MSBuild SDK drives npm.
Write-Head "Node.js"
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    $nodeVersion = (& node --version).TrimStart('v')
    if ([version]($nodeVersion) -ge [version]'20.19') {
        Write-Ok "v$nodeVersion"
    } else {
        Write-Miss "v$nodeVersion is too old (Vite 8 needs 20.19+, 22 LTS recommended)"
        if (Confirm-Action "Install the current Node.js LTS?") {
            Install-WithWinget -Id 'OpenJS.NodeJS.LTS' -Name 'Node.js LTS' | Out-Null
        } else { $script:Skipped += 'Node.js' }
    }
} else {
    Write-Miss "node not found"
    if (Confirm-Action "Install Node.js LTS?") {
        Install-WithWinget -Id 'OpenJS.NodeJS.LTS' -Name 'Node.js LTS' | Out-Null
    } else { $script:Skipped += 'Node.js' }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { $script:Missing += 'Node.js' }
}

# ---- vcpkg -------------------------------------------------------------------------
Write-Head "vcpkg"
$resolvedVcpkg = Resolve-VcpkgRoot -Preferred $VcpkgRoot
if ($resolvedVcpkg) {
    Write-Ok "root: $resolvedVcpkg"
} else {
    $target = if ($VcpkgRoot) { $VcpkgRoot } else { "C:\vcpkg" }
    Write-Miss "vcpkg not found"
    Write-Note "It will be cloned to $target and bootstrapped (a few minutes)."
    if (Confirm-Action "Install vcpkg into $target ?") {
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
            Write-Bad "git is required to fetch vcpkg."
        } else {
            Write-Step "cloning vcpkg"
            $code = Invoke-Native { & git clone https://github.com/microsoft/vcpkg.git $target }
            if ($code -eq 0) {
                Write-Step "bootstrapping vcpkg (compiles the tool itself)"
                $code = Invoke-Native { & (Join-Path $target "bootstrap-vcpkg.bat") -disableMetrics }
            }
            if ($code -eq 0) {
                $resolvedVcpkg = (Resolve-Path $target).Path
                Write-Ok "vcpkg ready at $resolvedVcpkg"
            }
        }
    } else { $script:Skipped += 'vcpkg' }
    if (-not $resolvedVcpkg) { $script:Missing += 'vcpkg' }
}

if ($resolvedVcpkg) {
    # Set before anything reads it; Enter-MsvcEnvironment above already preserves it.
    $env:VCPKG_ROOT = $resolvedVcpkg

    Write-Head "vcpkg ports ($Triplet)"
    $missingPorts = Get-MissingPorts -Root $resolvedVcpkg
    foreach ($port in $VcpkgPorts) {
        if ($missingPorts -contains $port) { Write-Miss $port } else { Write-Ok $port }
    }

    if ($missingPorts.Count -gt 0) {
        Write-Note "Building these from source takes a long time — ffmpeg and grpc dominate."
        if (Confirm-Action "Install $($missingPorts.Count) missing port(s)?") {
            $spec = $missingPorts | ForEach-Object { "$_`:$Triplet" }
            Write-Step "vcpkg install $($spec -join ' ')"
            Write-Note "this is the slow part — expect tens of minutes on a cold cache"
            $code = Invoke-Native { & (Join-Path $resolvedVcpkg "vcpkg.exe") install @spec }
            if ($code -ne 0) { $script:Missing += 'vcpkg ports' }
            else {
                $stillMissing = Get-MissingPorts -Root $resolvedVcpkg
                if ($stillMissing.Count -gt 0) { $script:Missing += "vcpkg ports: $($stillMissing -join ', ')" }
            }
        } else {
            $script:Skipped += "vcpkg ports: $($missingPorts -join ', ')"
            $script:Missing += 'vcpkg ports'
        }
    }
}

# ================================================================================ report

Write-Section "Prerequisites summary"
if ($script:Skipped.Count -gt 0) {
    Write-Host "  skipped by choice:" -ForegroundColor Yellow
    $script:Skipped | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
}
if ($script:Missing.Count -gt 0) {
    Write-Host "  cannot build, still missing:" -ForegroundColor Red
    $script:Missing | Sort-Object -Unique | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
} else {
    Write-Host "  all build prerequisites are present." -ForegroundColor Green
}

if ($CheckOnly) {
    Write-Note "check only; nothing was installed or built"
    exit ($(if ($script:Missing.Count) { 1 } else { 0 }))
}
if ($script:Missing.Count -gt 0) { exit 1 }
if ($SkipBuild) {
    Write-Note "-SkipBuild given; stopping here"
    exit 0
}

# ================================================================================= build

# A running service keeps its assemblies locked, and Windows refuses to overwrite them:
# the build would fail with MSB3021 "file is locked by .NET Host" rather than pick up the
# change. Say so plainly instead of letting the build die halfway.
$running = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '[\\/](Vms[\w\.]*|dotnet\.Server)\.dll' })
$running += @(Get-CimInstance Win32_Process -Filter "Name='vms.exe'" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Section "Build"
    Write-Bad "$($running.Count) ta_vms process(es) are running; their binaries are locked."
    Write-Note "Stop the stack (or the debug session) and run this again."
    exit 1
}

Write-Head "Building video_srv ($Configuration)"
$buildDir = Join-Path $VideoSrvDir "build"

# Built as a string first: PowerShell does not expand a $variable inside a native-command
# token that starts with "-D", so writing -DCMAKE_BUILD_TYPE=$Configuration inline puts the
# literal text "$Configuration" into the CMake cache and ninja then fails to parse its own
# rules file.
$buildTypeArg = "-DCMAKE_BUILD_TYPE=$Configuration"

Push-Location $VideoSrvDir
try {
    # windows-default reads $env{VCPKG_ROOT}, so the checked-in preset works on any machine
    # and no file in the repository has to be edited.
    Write-Step "cmake configure (preset windows-default, vcpkg: $env:VCPKG_ROOT)"
    $code = Invoke-Native { & cmake --preset windows-default $buildTypeArg }
    if ($code -ne 0) { throw "cmake configure failed ($code)" }
    Write-Ok "configured"
} finally { Pop-Location }

Write-Step "compiling and linking target 'vms'"
$code = Invoke-Native { & cmake --build $buildDir --target vms }
if ($code -ne 0) { throw "video_srv build failed ($code)" }

$exe = Join-Path $buildDir "vms.exe"
if (-not (Test-Path $exe)) { throw "vms.exe was not produced" }
$dllCount = @(Get-ChildItem $buildDir -Filter *.dll -ErrorAction SilentlyContinue).Count
Write-Ok "$exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB, $dllCount runtime DLLs beside it)"

Write-Head "Building the .NET solution ($Configuration)"
Write-Step "dotnet build $(Split-Path $Solution -Leaf) (16 projects, incl. the React client)"
$code = Invoke-Native { & dotnet build $Solution -c $Configuration --nologo }
if ($code -ne 0) { throw ".NET build failed ($code)" }
Write-Ok "solution built"

# ------------------------------------------------------------------------------ results
Write-Section "Done"
Write-Host "  total time: $("{0:n1}" -f $script:TotalWatch.Elapsed.TotalMinutes) min" -ForegroundColor Green
Write-Host "  artifacts:" -ForegroundColor Green
Write-Host "    $exe" -ForegroundColor Green
Write-Host "    $(Join-Path $RepoRoot "dotnet\**\bin\$Configuration\net10.0\")" -ForegroundColor Green
Write-Note "To run the stack you also need Docker and the Dapr CLI ('dapr init'), which this script does not touch."
Write-Note "From VS Code: the 'Full stack (F5) [Windows]' compound."
