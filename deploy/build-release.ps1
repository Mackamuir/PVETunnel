<#
.SYNOPSIS
  Builds the pveTunnel release artifacts and a manifest, on the Windows dev host.

.DESCRIPTION
  EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM

  Produces the two self-contained binaries the pipeline needs and a manifest recording
  version, source commit, runtime, size and SHA-256 for each, so a node or a template can
  be traced back to the source it was built from.

  Runs the test suite first by default. A release built from a tree that does not pass its
  own tests is not worth tracing.

.PARAMETER OutputDir
  Where to place the release. Defaults to ./release under the repository.

.PARAMETER SkipTests
  Skip the test run. For iterating only; do not ship an artifact built this way.

.EXAMPLE
  ./deploy/build-release.ps1
  ./deploy/build-release.ps1 -OutputDir D:\drops\pve-tunnel -SkipTests
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$OutputDir = if ($OutputDir) { $OutputDir } else { Join-Path $repo 'release' }

Push-Location $repo
try {
    $version = '0.2.0'
    $commit = (git rev-parse --short HEAD 2>$null)
    $dirty = (git status --porcelain 2>$null)
    if ($dirty) { $commit = "$commit-dirty" }

    Write-Host "pveTunnel release $version ($commit)" -ForegroundColor Cyan
    Write-Host 'EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM'

    if (-not $SkipTests) {
        Write-Host "`nRunning tests..." -ForegroundColor Cyan
        dotnet test pve-tunnel.slnx --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed; refusing to build a release.' }
    }
    else {
        Write-Warning 'Tests skipped. Do not deploy this artifact.'
    }

    if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
    $null = New-Item -ItemType Directory -Force -Path $OutputDir

    # The published assembly name stays stable; the installed copy is renamed by the installers
    # so it is unmistakable in a process listing. Those are two different things on purpose.
    $targets = @(
        @{ Project = 'PveTunnel.Relay';  Rid = 'linux-x64'; Produces = 'pve-tunnel-relay';     Role = 'relay (Proxmox node)' }
        @{ Project = 'PveTunnel.Bridge'; Rid = 'win-x64';   Produces = 'pve-tunnel-bridge.exe'; Role = 'bridge (Windows guest)' }
    )

    $entries = @()
    foreach ($t in $targets) {
        Write-Host "`nPublishing $($t.Project) for $($t.Rid)..." -ForegroundColor Cyan
        $stage = Join-Path $OutputDir "stage-$($t.Rid)-$($t.Project)"
        dotnet publish "$($t.Project)/$($t.Project).csproj" `
            -c Release -r $t.Rid --self-contained true `
            -p:PublishSingleFile=true -p:DebugType=none `
            -o $stage --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($t.Project)." }

        $src = Join-Path $stage $t.Produces
        if (-not (Test-Path $src)) { throw "Expected artifact not produced: $src" }

        $dest = Join-Path $OutputDir $t.Produces
        Move-Item -LiteralPath $src -Destination $dest -Force
        Remove-Item -Recurse -Force $stage

        $hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item -LiteralPath $dest).Length
        $entries += [ordered]@{
            file    = $t.Produces
            role    = $t.Role
            runtime = $t.Rid
            bytes   = $size
            sha256  = $hash
        }
        Write-Host ("  {0}  {1:N0} bytes  {2}" -f $t.Produces, $size, $hash)
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vms.example.json') -Destination $OutputDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-relay-node.sh') -Destination $OutputDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pve-tunnel-vm.sh') -Destination $OutputDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'smoke-test.sh') -Destination $OutputDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-bridge-windows.ps1') -Destination $OutputDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pve-tunnel-relay.service') -Destination $OutputDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pve-tunnel.conf') -Destination $OutputDir -Force

    $manifest = [ordered]@{
        notice     = 'EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM'
        component  = 'pveTunnel'
        version    = $version
        commit     = $commit
        built_utc  = (Get-Date).ToUniversalTime().ToString('o')
        built_by   = "$env:USERNAME@$env:COMPUTERNAME"
        sdk        = (dotnet --version)
        tested     = (-not $SkipTests.IsPresent)
        artifacts  = $entries
    }
    $manifestPath = Join-Path $OutputDir 'EXERCISE-OUT-OF-SCOPE-pveTunnel-release-manifest.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    'EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM' |
        Set-Content -LiteralPath (Join-Path $OutputDir 'EXERCISE-OUT-OF-SCOPE.txt') -Encoding utf8

    Write-Host "`nRelease written to $OutputDir" -ForegroundColor Green
    Write-Host 'Next: copy the relay binary, install-relay-node.sh, pve-tunnel-vm.sh and smoke-test.sh to the Proxmox node.'
}
finally {
    Pop-Location
}
