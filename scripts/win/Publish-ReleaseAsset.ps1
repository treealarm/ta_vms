#Requires -Version 7
<#
.SYNOPSIS
  Puts a file into a prerelease of this repository as its asset, creating the prerelease first if
  there is none. Used by .github/workflows/windows.yml for both windows-deps (the vcpkg binary
  cache) and windows-build (what ta_install's Windows installer is built from).

.DESCRIPTION
  The asset is replaced with --clobber, which deletes the old one before the new one is in place:
  a failed upload leaves the release without it, and the step fails. Nothing else about the release
  is touched until the upload has succeeded, so its notes never describe a file it does not hold.

  -Rolling is for a release that follows the newest build of master. Its notes must name the commit
  as a full SHA; after the upload they are replaced and the tag is moved to -Commit, so that the
  release page shows the commit the asset was built from. A commit that is not ahead of the one
  already published is refused: re-running an old workflow run must not replace a newer build.
#>
param(
  [Parameter(Mandatory)] [string] $Release,
  [Parameter(Mandatory)] [string] $Path,
  [Parameter(Mandatory)] [string] $Title,
  [Parameter(Mandatory)] [string] $Notes,
  [string] $Commit = $env:GITHUB_SHA,
  [string] $Repo = $env:GITHUB_REPOSITORY,
  [switch] $Rolling
)

$ErrorActionPreference = 'Stop'

function Invoke-Gh {
  & gh @args
  if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed with $LASTEXITCODE" }
}

$body = gh release view $Release --repo $Repo --json body -q .body 2>$null
$exists = $LASTEXITCODE -eq 0

if ($Rolling -and $exists) {
  $published = [regex]::Match($body, '\b[0-9a-f]{40}\b').Value
  if ($published -and $published -ne $Commit) {
    $status = gh api "repos/$Repo/compare/$published...$Commit" -q .status
    if ($LASTEXITCODE -ne 0) { throw "cannot compare $published with $Commit" }
    if ($status -ne 'ahead') {
      "::notice::$Release holds $published; $Commit is $status of it, so it is not replaced."
      return
    }
  }
}

if (-not $exists) {
  Invoke-Gh release create $Release --repo $Repo --prerelease --target $Commit --title $Title --notes $Notes
}
Invoke-Gh release upload $Release $Path --repo $Repo --clobber
if ($Rolling -and $exists) {
  Invoke-Gh release edit $Release --repo $Repo --notes $Notes
  Invoke-Gh api -X PATCH "repos/$Repo/git/refs/tags/$Release" -f "sha=$Commit" -F force=true --silent
}
"$Release`: $([IO.Path]::GetFileName($Path)) published from $Commit"
