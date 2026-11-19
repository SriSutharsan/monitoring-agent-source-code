<#
.SYNOPSIS
  Removes the Endpoint Monitoring Agent service and program files.
.PARAMETER RemoveData
  Also deletes the local database, logs, identity and stored credential under %ProgramData%\EndpointAgent.
  Without it the data is kept, so a later re-install resumes with the same device identity.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'AgentInstall.Common.ps1')

try {
    Assert-Administrator
    if (-not $InstallDir) { $InstallDir = $script:DefaultInstallDir }

    Write-Step 'Stopping the service'
    Stop-AgentService

    if (Get-AgentService) {
        Write-Step 'Removing the service'
        & sc.exe delete $script:ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }

    if (Test-Path $InstallDir) {
        Write-Step ('Removing ' + $InstallDir)
        # this script may itself live in the install folder: copy it out of the way before deleting
        $self = $MyInvocation.MyCommand.Path
        if ($self -and $self.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)) {
            $tmp = Join-Path $env:TEMP ('agent-uninstall-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $tmp | Out-Null
            Get-ChildItem $InstallDir -Filter '*.ps1' | ForEach-Object { Copy-Item $_.FullName $tmp }
        }
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($RemoveData -and (Test-Path $script:DataDir)) {
        Write-Step ('Removing data in ' + $script:DataDir)
        Remove-Item -Path $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue
    } elseif (Test-Path $script:DataDir) {
        Write-Host ('    Data kept in ' + $script:DataDir + ' (use -RemoveData to delete it).')
    }

    try { if ([Diagnostics.EventLog]::SourceExists($script:ServiceName)) { [Diagnostics.EventLog]::DeleteEventSource($script:ServiceName) } } catch { }
    Write-Ok 'Endpoint Monitoring Agent was removed. The device record on the monitoring server is not deleted; remove it there if required.'
}
catch {
    Write-Host ('[x] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
