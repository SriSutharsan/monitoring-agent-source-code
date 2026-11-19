# Shared helpers for install.ps1 / uninstall.ps1 / update.ps1.
# Written for Windows PowerShell 2.0 (Windows 7 SP1) and newer: no Expand-Archive, ConvertFrom-Json, Get-CimInstance or [ordered].

$script:ServiceName = 'EndpointMonitoringAgent'
$script:DisplayName = 'Endpoint Monitoring Agent'
$script:DefaultInstallDir = Join-Path $env:ProgramFiles 'EndpointAgent'
$script:DataDir = Join-Path $env:ProgramData 'EndpointAgent'

function Write-Step([string]$Message) { Write-Host ('[*] ' + $Message) }
function Write-Ok([string]$Message)   { Write-Host ('[+] ' + $Message) -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host ('[!] ' + $Message) -ForegroundColor Yellow }

function Assert-Administrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated (Run as administrator) PowerShell window.'
    }
}

function Get-OsInfo {
    $v = [Environment]::OSVersion.Version
    $sp = 0
    try { $sp = (Get-WmiObject Win32_OperatingSystem).ServicePackMajorVersion } catch { }
    # [Environment]::Is64BitOperatingSystem does not exist on the CLR 2.0 that Windows PowerShell 2.0 runs on
    $is64 = ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') -or [bool]$env:PROCESSOR_ARCHITEW6432
    $name = 'Windows'
    if ($v.Major -eq 6 -and $v.Minor -eq 1) { $name = 'Windows 7' }
    elseif ($v.Major -eq 6 -and $v.Minor -ge 2) { $name = 'Windows 8/8.1' }
    elseif ($v.Major -eq 10 -and $v.Build -ge 22000) { $name = 'Windows 11' }
    elseif ($v.Major -eq 10) { $name = 'Windows 10' }
    New-Object PSObject -Property @{ Name = $name; Major = $v.Major; Minor = $v.Minor; Build = $v.Build; ServicePack = $sp; Is64 = $is64 }
}

function Test-OsSupported($os) {
    if ($os.Major -lt 6) { return $false }
    if ($os.Major -eq 6 -and $os.Minor -eq 0) { return $false }                   # Vista
    if ($os.Major -eq 6 -and $os.Minor -eq 1 -and $os.ServicePack -lt 1) { return $false }   # Windows 7 needs SP1
    return $true
}

# net8 build: Windows 10 1607+ / Windows 11, 64-bit. Everything else (Windows 7 SP1, 8.1, older 10, 32-bit) uses the .NET Framework 4.8 build.
function Get-RuntimeChoice($os) {
    if ($os.Is64 -and $os.Major -eq 10 -and $os.Build -ge 14393) { return 'net8' }
    return 'net48'
}

function Test-DotNetFramework48 {
    $key = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
    try { $rel = (Get-ItemProperty -Path $key -ErrorAction Stop).Release; return ($rel -ge 528040) } catch { return $false }
}

function Set-Tls12Enabled {
    # Windows 7 SP1: TLS 1.1/1.2 exist but are not enabled for client connections by default.
    $base = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client'
    if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }
    New-ItemProperty -Path $base -Name 'DisabledByDefault' -Value 0 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $base -Name 'Enabled' -Value 1 -PropertyType DWord -Force | Out-Null
    foreach ($k in @('HKLM:\SOFTWARE\Microsoft\.NET Framework\v4.0.30319', 'HKLM:\SOFTWARE\Wow6432Node\Microsoft\.NET Framework\v4.0.30319')) {
        if (Test-Path $k) {
            New-ItemProperty -Path $k -Name 'SchUseStrongCrypto' -Value 1 -PropertyType DWord -Force | Out-Null
            New-ItemProperty -Path $k -Name 'SystemDefaultTlsVersions' -Value 1 -PropertyType DWord -Force | Out-Null
        }
    }
}

function Test-Tls12Enabled {
    try {
        $p = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client' -ErrorAction Stop
        return ($p.Enabled -eq 1 -and $p.DisabledByDefault -eq 0)
    } catch { return $false }
}

function Get-AgentService { Get-WmiObject Win32_Service -Filter ("Name='" + $script:ServiceName + "'") -ErrorAction SilentlyContinue }

function Stop-AgentService([int]$TimeoutSeconds = 60) {
    $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $script:ServiceName -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            $svc.Refresh()
            if ($svc.Status -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }
        if ($svc.Status -ne 'Stopped') {
            # last resort: the service ignored the stop request
            Get-Process -Name 'Agent.Service' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
}

# Only SYSTEM and Administrators may read or change the agent's data (database, credential, registration token).
# SIDs are used instead of names so this works on every Windows language.
function Set-RestrictedAcl([string]$Path) {
    & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ('icacls failed for ' + $Path) }
}

function Write-JsonFile([string]$Path, $Object) {
    Add-Type -AssemblyName System.Web.Extensions
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = 10485760
    $json = $ser.Serialize($Object)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = $Path + '.tmp'
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path $Path) { Remove-Item $Path -Force }
    Move-Item $tmp $Path
}

function Read-JsonFile([string]$Path) {
    Add-Type -AssemblyName System.Web.Extensions
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = 10485760
    $ser.DeserializeObject([IO.File]::ReadAllText($Path))
}

function Expand-Zip([string]$ZipPath, [string]$Destination) {
    if (-not (Test-Path $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop       # .NET 4.5+, always present once 4.8 is installed
        [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Destination)
    } catch {
        $shell = New-Object -ComObject Shell.Application
        $zip = $shell.NameSpace($ZipPath)
        $shell.NameSpace($Destination).CopyHere($zip.Items(), 16)
    }
}

function Get-FileSha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { $hash = $sha.ComputeHash($stream) } finally { $stream.Dispose(); $sha.Dispose() }
    (($hash | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Register-AgentEventSource {
    try {
        if (-not [Diagnostics.EventLog]::SourceExists($script:ServiceName)) {
            [Diagnostics.EventLog]::CreateEventSource($script:ServiceName, 'Application')
        }
    } catch { Write-Warn ('Could not register the Event Log source: ' + $_.Exception.Message) }
}

function Install-AgentService([string]$InstallDir) {
    $exe = Join-Path $InstallDir 'Agent.Service.exe'
    $existing = Get-AgentService
    if ($existing) {
        & sc.exe config $script:ServiceName binPath= ('"' + $exe + '"') | Out-Null
    } else {
        & sc.exe create $script:ServiceName binPath= ('"' + $exe + '"') DisplayName= $script:DisplayName start= delayed-auto obj= LocalSystem | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'sc.exe create failed' }
    }
    & sc.exe config $script:ServiceName start= delayed-auto | Out-Null
    & sc.exe description $script:ServiceName 'Collects hardware, software, security and performance information for IT administration and reports it to the configured monitoring service.' | Out-Null
    # restart after 1, 2 and 5 minutes; reset the failure counter after a day; also act when the process exits with an error code
    & sc.exe failure $script:ServiceName reset= 86400 actions= restart/60000/restart/120000/restart/300000 | Out-Null
    & sc.exe failureflag $script:ServiceName 1 | Out-Null
}

function Wait-ServiceRunning([int]$TimeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}
