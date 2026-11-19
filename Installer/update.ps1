<#
.SYNOPSIS
  Applies a verified agent update package and rolls back automatically if the new version does not start.
.DESCRIPTION
  Normally launched by the agent itself after it verified the signed update manifest and the package hash. It can also be run by
  an administrator. The package is a zip with the same layout as the installation package (net48\ and net8\ folders).
  Configuration, identity, credential and the queued data live outside the install folder and are never touched.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$Sha256,
    [string]$ExpectedVersion,
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'AgentInstall.Common.ps1')

$work = Join-Path $env:TEMP ('agent-update-' + [Guid]::NewGuid().ToString('N'))
$backup = $null
$logFile = Join-Path (Join-Path $script:DataDir 'logs') 'update.log'
function Log([string]$m) { try { Add-Content -Path $logFile -Value ((Get-Date).ToString('s') + ' ' + $m) } catch { }; Write-Host $m }

try {
    Assert-Administrator
    if (-not $InstallDir) { $InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $exe = Join-Path $InstallDir 'Agent.Service.exe'
    $failedMarker = Join-Path (Join-Path $script:DataDir 'updates') ('failed-' + ($ExpectedVersion -replace '[^A-Za-z0-9\.\-]', '_') + '.txt')

    Log ('Update to ' + $ExpectedVersion + ' started')
    if (-not (Test-Path $PackagePath)) { throw 'Package not found.' }
    $actual = Get-FileSha256 $PackagePath
    if ($actual -ne $Sha256.ToLowerInvariant()) { throw 'Package hash does not match the verified hash; update aborted.' }

    $runtime = 'net48'
    $marker = Join-Path $InstallDir 'runtime.txt'
    if (Test-Path $marker) { $runtime = (Get-Content $marker | Select-Object -First 1).Trim() }

    Expand-Zip $PackagePath $work
    $newSource = Join-Path $work $runtime
    if (-not (Test-Path (Join-Path $newSource 'Agent.Service.exe'))) { throw ('Package does not contain ' + $runtime + '\Agent.Service.exe') }

    # back up the current program files
    $backup = $InstallDir + '.backup'
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    Copy-Item -Path $InstallDir -Destination $backup -Recurse -Force

    Stop-AgentService
    Copy-Item -Path (Join-Path $newSource '*') -Destination $InstallDir -Recurse -Force
    foreach ($f in @('update.ps1', 'uninstall.ps1', 'AgentInstall.Common.ps1')) {
        $p = Join-Path $work $f
        if (Test-Path $p) { Copy-Item $p $InstallDir -Force }
    }
    Set-RestrictedAcl $InstallDir

    Start-Service -Name $script:ServiceName
    $healthy = Wait-ServiceRunning 60
    if ($healthy) {
        Start-Sleep -Seconds 20                         # a crash loop shows up within seconds of starting
        $svc = Get-Service -Name $script:ServiceName
        $healthy = ($svc.Status -eq 'Running')
    }
    if ($healthy -and $ExpectedVersion) {
        $ver = (& $exe --version) -join ' '
        if ($ver -notmatch [Regex]::Escape($ExpectedVersion)) { $healthy = $false; Log ('Version check failed, got: ' + $ver) }
    }
    if (-not $healthy) { throw 'The updated agent did not start correctly.' }

    Log 'Update completed'
    Remove-Item $PackagePath -Force -ErrorAction SilentlyContinue
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    Log ('Update failed: ' + $_.Exception.Message)
    if ($backup -and (Test-Path $backup)) {
        Log 'Rolling back to the previous version'
        try {
            Stop-AgentService
            Copy-Item -Path (Join-Path $backup '*') -Destination $InstallDir -Recurse -Force
            Start-Service -Name $script:ServiceName
            Log 'Rollback completed'
        } catch { Log ('Rollback failed: ' + $_.Exception.Message) }
    }
    try { Set-Content -Path $failedMarker -Value ((Get-Date).ToString('s') + ' ' + $_.Exception.Message) } catch { }   # stops the agent offering the same package again
    exit 1
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
