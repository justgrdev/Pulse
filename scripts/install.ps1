# Run once from an elevated PowerShell.
# Creates a scheduled task that starts Pulse with admin rights (no UAC prompt)
# and a Start menu shortcut that runs it. -Autostart also starts it at logon.
param([switch]$Autostart)
$ErrorActionPreference = 'Stop'

$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\Pulse.exe'
if (-not (Test-Path $exe)) { & "$PSScriptRoot\publish.ps1" }
if (-not (Test-Path $exe)) { throw "dist\Pulse.exe not found" }

$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$task = @{
    TaskName  = 'Pulse'
    Action    = New-ScheduledTaskAction -Execute $exe -Argument '--no-elevate'
    Principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    Settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Force     = $true
}
if ($Autostart) { $task.Trigger = New-ScheduledTaskTrigger -AtLogOn -User $user }
Register-ScheduledTask @task | Out-Null

$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Pulse.lnk'))
$lnk.TargetPath = "$env:WINDIR\System32\schtasks.exe"
$lnk.Arguments = '/run /tn Pulse'
$lnk.IconLocation = "$exe,0"
$lnk.WindowStyle = 7
$lnk.Save()
"Installed. Start Pulse from the Start menu."
