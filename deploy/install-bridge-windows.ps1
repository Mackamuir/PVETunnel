#requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the pveTunnel bridge on a Windows guest and optionally configures a client application.

.DESCRIPTION
  - Installs the published bridge with an unmistakable EXERCISE / OUT-OF-SCOPE filename under
    C:\Program Files\EXERCISE-OUT-OF-SCOPE\pveTunnel
  - Creates labelled data, log, and notice artifacts under
    C:\ProgramData\EXERCISE-OUT-OF-SCOPE\pveTunnel
  - Registers a visibly labelled Scheduled Task that runs it at boot as SYSTEM
  - Optionally sets a machine-wide environment variable so an application targets the bridge

  Run once inside the VM template, then sysprep/seal. Every clone self-wires.

.PARAMETER Source
  Folder containing the published bridge (pve-tunnel-bridge.exe). Default: .\bridge

.PARAMETER ClientEnvironmentVariable
  Machine-wide environment variable to set for the tunneled application. Default: BASE_URL.

.PARAMETER ClientEnvironmentValue
  Value assigned to ClientEnvironmentVariable. Default: http://127.0.0.1:5000/api.

.NOTES
  Requires the virtio-serial driver (vioser) from the virtio-win ISO to be installed first,
  so \\.\Global\pve-tunnel.relay.0 exists when the VM has the virtio-serial device.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot 'bridge'),
    [string]$InstallDir = 'C:\Program Files\EXERCISE-OUT-OF-SCOPE\pveTunnel',
    [string]$DataDir = 'C:\ProgramData\EXERCISE-OUT-OF-SCOPE\pveTunnel',
    [string]$ClientEnvironmentVariable = 'BASE_URL',
    [string]$ClientEnvironmentValue = 'http://127.0.0.1:5000/api'
)

$ErrorActionPreference = 'Stop'
$publishedExeName = 'pve-tunnel-bridge.exe'
$installedExeName = 'EXERCISE-OUT-OF-SCOPE-pveTunnel-bridge.exe'
$noticeName = 'EXERCISE-OUT-OF-SCOPE.txt'
$logName = 'EXERCISE-OUT-OF-SCOPE-pveTunnel-bridge.log'
$scopeNotice = 'EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM'
$srcExe = Join-Path $Source $publishedExeName
if (-not (Test-Path $srcExe)) { throw "Bridge exe not found at $srcExe. Publish it first (see README)." }

New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
$exe = Join-Path $InstallDir $installedExeName
Copy-Item -LiteralPath $srcExe -Destination $exe -Force
Get-ChildItem -LiteralPath $Source -Force |
    Where-Object Name -ne $publishedExeName |
    Copy-Item -Destination $InstallDir -Recurse -Force

$noticeText = "$scopeNotice`r`n`r`nThis pveTunnel component is exercise infrastructure and is not a Blue Team investigation target.`r`n"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $InstallDir $noticeName), $noticeText, $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $DataDir $noticeName), $noticeText, $utf8NoBom)
$logPath = Join-Path $DataDir $logName
Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) Installed $exe" -Encoding UTF8

# Mark the component as exercise infrastructure and point the client application at the
# loopback bridge when requested.
[Environment]::SetEnvironmentVariable('EXERCISE_PVE_TUNNEL_NOTICE', $scopeNotice, 'Machine')
if ($ClientEnvironmentVariable) {
    [Environment]::SetEnvironmentVariable($ClientEnvironmentVariable, $ClientEnvironmentValue, 'Machine')
}

# ---------------------------------------------------------------------------------------
# Verify the virtio-serial plumbing the bridge depends on.
#
# The driver ships in virtio-win-gt-x64.msi, which Ludus's packer build already installs, but
# nothing has ever checked that it took. A missing vioser driver produces no error until the
# bridge cannot open the port at first boot, which is a bad place to find out.
#
# The port itself is deliberately NOT required here: during a Packer build the QEMU device is
# not attached yet, and the template must still seal successfully. Missing driver is a warning
# that matters; missing port at build time is expected.
# ---------------------------------------------------------------------------------------
$driverFound = $false
try {
    $driverFound = [bool](pnputil /enum-drivers 2>$null | Select-String -SimpleMatch 'vioser.inf')
} catch { }
if (-not $driverFound) {
    try {
        $driverFound = [bool](Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop |
            Where-Object { $_.DeviceName -like '*VirtIO*Serial*' -or $_.InfName -like 'vioser*' })
    } catch { }
}

if ($driverFound) {
    Write-Host "virtio-serial (vioser) driver present."
} else {
    Write-Warning ("virtio-serial (vioser) driver NOT found. The bridge will install, but it " +
        "cannot open the channel without it. Install virtio-win-gt-x64.msi in the template " +
        "before sealing, then re-run this check.")
}

$portPath = '\.\Global\pve-tunnel.relay.0'
if (Test-Path $portPath) {
    Write-Host "virtio-serial port is present at $portPath."
} else {
    Write-Host ("virtio-serial port not present yet at $portPath. Expected during a Packer " +
        "build, where the QEMU device is attached only to the cloned VM.")
}

# Run at startup as SYSTEM.
$taskPath = '\EXERCISE - OUT OF SCOPE\'
$taskName = 'EXERCISE - pveTunnel bridge - OUT OF SCOPE'
$taskDescription = 'EXERCISE INFRASTRUCTURE - pveTunnel bridge - OUT OF SCOPE FOR BLUE TEAM'
$taskAuthor = 'EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM'
$taskSource = 'EXERCISE-OUT-OF-SCOPE-pveTunnel'
$taskFullName = "$taskPath$taskName"

# Remove both the old unlabelled task and any previous labelled installation. Not schtasks.exe:
# with $ErrorActionPreference = 'Stop', a non-console host (WinRM, so Ansible) turns its
# "task not found" stderr into a terminating error, failing every first install.
Get-ScheduledTask -TaskPath '\' -TaskName 'PveTunnelBridge' -ErrorAction SilentlyContinue |
    Unregister-ScheduledTask -Confirm:$false
Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue |
    Unregister-ScheduledTask -Confirm:$false

# Register-ScheduledTask requires the folder to exist. Create it through the Task Scheduler API.
$scheduleService = New-Object -ComObject 'Schedule.Service'
$scheduleService.Connect()
$rootFolder = $scheduleService.GetFolder('\')
try {
    $null = $scheduleService.GetFolder($taskPath.TrimEnd('\'))
} catch {
    $null = $rootFolder.CreateFolder($taskPath.Trim('\'))
}

$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
# No execution time limit: the default (72 hours) kills the bridge, and RestartCount does not
# restart a task that Task Scheduler itself stopped.
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Description $taskDescription -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# Author and Source are Task Scheduler registration-info fields not exposed as direct parameters
# by Register-ScheduledTask. Update the registered definition through the Task Scheduler API.
$taskFolder = $scheduleService.GetFolder($taskPath.TrimEnd('\'))
$registeredTask = $taskFolder.GetTask($taskName)
$definition = $registeredTask.Definition
$definition.RegistrationInfo.Author = $taskAuthor
$definition.RegistrationInfo.Description = $taskDescription
$definition.RegistrationInfo.Source = $taskSource
$taskCreateOrUpdate = 6
$taskLogonServiceAccount = 5
$null = $taskFolder.RegisterTaskDefinition(
    $taskName,
    $definition,
    $taskCreateOrUpdate,
    'SYSTEM',
    $null,
    $taskLogonServiceAccount,
    $null
)

Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName
$environmentMessage = if ($ClientEnvironmentVariable) {
    ", set $ClientEnvironmentVariable=$ClientEnvironmentValue"
} else {
    ''
}
Write-Host "Installed $scopeNotice bridge to $exe$environmentMessage, registered + started task '$taskFullName'."
