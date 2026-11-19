<#
.SYNOPSIS
  Installs (or upgrades) the Endpoint Monitoring Agent as a Windows service.
.DESCRIPTION
  Supports Windows 7 SP1, 8.1, 10 and 11. Picks the .NET Framework 4.8 build or the self-contained .NET 8 build automatically.
  Run from an elevated PowerShell window. Safe to re-run: an existing installation is upgraded in place and keeps its identity,
  credential and queued data.
.EXAMPLE
  .\install.ps1 -ApiUrl https://monitor.example.com -RegistrationToken abc123 -Group "Finance" -SigningPublicKeyFile .\config-signing.pub.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ApiUrl,
    [string]$RegistrationToken,
    [string]$Group,
    [string]$SigningPublicKeyFile,
    [string]$InstallDir,
    [string]$PackageRoot,
    [ValidateSet('auto', 'net48', 'net8')][string]$Runtime = 'auto',
    [ValidateSet('Debug', 'Information', 'Warning', 'Error')][string]$LogLevel = 'Information',
    [string]$ProxyUrl,
    [string[]]$PinnedCertificateSha256,
    [string[]]$PingTarget,
    [switch]$AllowInsecureLoopback,
    [switch]$SkipTlsFix,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'AgentInstall.Common.ps1')

try {
    Assert-Administrator
    if (-not $InstallDir) { $InstallDir = $script:DefaultInstallDir }
    if (-not $PackageRoot) { $PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }

    # ---------------------------------------------------------------- checks
    Write-Step 'Checking the system'
    $os = Get-OsInfo
    Write-Host ('    {0} (build {1}, SP{2}, {3})' -f $os.Name, $os.Build, $os.ServicePack, $(if ($os.Is64) { '64-bit' } else { '32-bit' }))
    if (-not (Test-OsSupported $os)) { throw 'Unsupported Windows version. Windows 7 SP1, 8.1, 10 and 11 are supported.' }

    $uri = $null
    if (-not [Uri]::TryCreate($ApiUrl, [UriKind]::Absolute, [ref]$uri)) { throw 'ApiUrl is not a valid absolute URL.' }
    if ($uri.Scheme -ne 'https' -and -not ($AllowInsecureLoopback -and $uri.IsLoopback)) { throw 'ApiUrl must use https:// (http is only allowed for loopback with -AllowInsecureLoopback, for development).' }

    $chosen = $Runtime
    if ($chosen -eq 'auto') { $chosen = Get-RuntimeChoice $os }
    if ($chosen -eq 'net8' -and -not ($os.Is64 -and $os.Major -eq 10 -and $os.Build -ge 14393)) { throw 'The net8 build needs 64-bit Windows 10 1607 or newer. Use -Runtime net48.' }
    Write-Host ('    Using the {0} build' -f $chosen)

    if ($chosen -eq 'net48' -and -not (Test-DotNetFramework48)) {
        throw '.NET Framework 4.8 is required on this computer. Install it from https://dotnet.microsoft.com/download/dotnet-framework/net48 (on Windows 7 SP1 also install KB4474419 / KB4490628 first) and run this script again.'
    }

    $source = Join-Path $PackageRoot $chosen
    if (-not (Test-Path (Join-Path $source 'Agent.Service.exe'))) { throw ('Agent.Service.exe not found in ' + $source) }

    if ($os.Major -eq 6 -and $os.Minor -eq 1 -and -not $SkipTlsFix -and -not (Test-Tls12Enabled)) {
        Write-Step 'Enabling TLS 1.2 for outgoing connections (required by HTTPS on Windows 7)'
        Set-Tls12Enabled
        Write-Warn 'TLS 1.2 was enabled in the registry. If the agent cannot connect after the first start, restart the computer once.'
    }

    # ---------------------------------------------------------------- stop old
    Write-Step 'Stopping any running agent'
    Stop-AgentService

    # ---------------------------------------------------------------- files
    Write-Step ('Copying files to ' + $InstallDir)
    if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
    Copy-Item -Path (Join-Path $source '*') -Destination $InstallDir -Recurse -Force
    foreach ($f in @('update.ps1', 'uninstall.ps1', 'AgentInstall.Common.ps1')) {
        $p = Join-Path $PackageRoot $f
        if (Test-Path $p) { Copy-Item $p $InstallDir -Force }
    }
    Set-Content -Path (Join-Path $InstallDir 'runtime.txt') -Value $chosen
    foreach ($dir in @($script:DataDir, (Join-Path $script:DataDir 'config'), (Join-Path $script:DataDir 'data'), (Join-Path $script:DataDir 'logs'), (Join-Path $script:DataDir 'secure'), (Join-Path $script:DataDir 'updates'))) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }
    Set-RestrictedAcl $script:DataDir
    Set-RestrictedAcl $InstallDir        # the agent runs as LocalSystem: normal users must not be able to replace its binaries

    # ---------------------------------------------------------------- configuration (kept outside the install folder so updates never overwrite it)
    Write-Step 'Writing configuration'
    $configPath = Join-Path (Join-Path $script:DataDir 'config') 'appsettings.local.json'
    $agent = @{}
    if (Test-Path $configPath) {
        try { $old = Read-JsonFile $configPath; if ($old.ContainsKey('Agent')) { $agent = $old['Agent'] } } catch { Write-Warn 'Existing configuration could not be read and was replaced.' }
    }
    if (-not $agent.ContainsKey('Api')) { $agent['Api'] = @{} }
    $agent['Api']['BaseUrl'] = $ApiUrl.TrimEnd('/')
    $agent['Api']['AllowInsecureLoopback'] = [bool]$AllowInsecureLoopback
    if ($ProxyUrl) { $agent['Api']['ProxyUrl'] = $ProxyUrl }
    if ($PinnedCertificateSha256) { $agent['Api']['PinnedCertificateSha256'] = @($PinnedCertificateSha256) }
    if (-not $agent.ContainsKey('Registration')) { $agent['Registration'] = @{} }
    if ($RegistrationToken) { $agent['Registration']['Token'] = $RegistrationToken }
    if ($Group) { $agent['Registration']['Group'] = $Group }
    if (-not $agent.ContainsKey('Logging')) { $agent['Logging'] = @{} }
    $agent['Logging']['MinimumLevel'] = $LogLevel
    if (-not $agent.ContainsKey('Security')) { $agent['Security'] = @{} }
    if ($SigningPublicKeyFile) {
        if (-not (Test-Path $SigningPublicKeyFile)) { throw ('Signing key file not found: ' + $SigningPublicKeyFile) }
        $agent['Security']['ConfigSigningPublicKey'] = [IO.File]::ReadAllText($SigningPublicKeyFile)
    }
    if ($PingTarget) {
        if (-not $agent.ContainsKey('Collection')) { $agent['Collection'] = @{} }
        $agent['Collection']['PingTargets'] = @($PingTarget)
    }
    if (-not $RegistrationToken -and -not ($agent['Registration'].ContainsKey('Token') -and $agent['Registration']['Token'])) {
        Write-Warn 'No -RegistrationToken was given and none is stored. The agent will collect data but cannot register until a token is configured.'
    }
    if (-not ($agent['Security'].ContainsKey('ConfigSigningPublicKey') -and $agent['Security']['ConfigSigningPublicKey'])) {
        Write-Warn 'No signing public key configured: signed remote configuration and automatic updates will be rejected (data collection and sync still work).'
    }
    Write-JsonFile $configPath @{ Agent = $agent }
    Set-RestrictedAcl $script:DataDir

    # ---------------------------------------------------------------- service
    Write-Step 'Registering the Windows service'
    Register-AgentEventSource
    Install-AgentService $InstallDir

    if ($NoStart) { Write-Ok 'Installed. The service was not started (-NoStart).'; return }

    Write-Step 'Starting the service'
    Start-Service -Name $script:ServiceName
    if (Wait-ServiceRunning 60) {
        Write-Ok ('Endpoint Monitoring Agent is running. Logs: ' + (Join-Path $script:DataDir 'logs'))
        Write-Host ('    Verify the connection with:  "' + (Join-Path $InstallDir 'Agent.Service.exe') + '" --check')
    } else {
        throw ('The service did not reach the Running state. See ' + (Join-Path $script:DataDir 'logs') + ' and the Windows Application event log (source EndpointMonitoringAgent).')
    }
}
catch {
    Write-Host ('[x] ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
