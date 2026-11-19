# Shared by start-cyd, stop-cyd and cyd-watchdog: where the server lives and how to launch it.
# Dot-sourced, never run on its own.

$cydRepo   = Split-Path -Parent $PSScriptRoot
# The release package keeps cyd-monitor.exe next to the scripts folder; a source checkout builds it
# under server\. The first one that exists wins.
$cydExe    = @(
    (Join-Path $cydRepo 'cyd-monitor.exe'),
    (Join-Path $cydRepo 'server\cyd-monitor\bin\Release\net10.0-windows\cyd-monitor.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cydExe) { $cydExe = Join-Path $cydRepo 'server\cyd-monitor\bin\Release\net10.0-windows\cyd-monitor.exe' }
$cydLogDir = Join-Path $env:LOCALAPPDATA 'cyd-monitor'

function Get-CydServer {
    Get-Process -Name cyd-monitor -ErrorAction SilentlyContinue
}

# Returns the new process id, or 0 on failure. The server is launched through WMI so it is
# neither a child of the shell that started it nor of the scheduled task: ending a session,
# closing a window or stopping the watchdog must not take it down (that is exactly how it died
# when a Claude Code session ended). ShowWindow 0 keeps it hidden - a visible console would sit
# on the desktop, and a click into it (selection mode) blocks the console writes of the frame
# loop. WMI-created processes run in the caller's interactive session, which Desktop
# Duplication needs, and at Normal priority.
function Start-CydServer {
    $si = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine              = '"' + $cydExe + '"'
        CurrentDirectory         = Split-Path $cydExe
        ProcessStartupInformation = $si
    }
    if ($r.ReturnValue -ne 0) { return 0 }
    [int]$r.ProcessId
}
