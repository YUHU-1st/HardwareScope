param(
    [string]$SetupPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release\1.2.0\HardwareScope-FileGuard-1.1.0-windows-x64-setup.exe'),
    [string]$ReportDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\verification\1.2.0')
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) { throw '此安装验证脚本需要管理员权限。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()
$exe = Join-Path $env:ProgramFiles 'HardwareScope FileGuard\HardwareScope.FileGuard.exe'
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
function Check([string]$Name, [bool]$Passed, $Detail) {
    $results.Add([pscustomobject]@{name=$Name;passed=$Passed;detail=$Detail})
    if (-not $Passed) { throw "$Name failed: $Detail" }
}
function Run([string]$File, [string]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
try {
    & reg.exe export 'HKLM\SYSTEM\CurrentControlSet\Services\HardwareScopeFileGuard' (Join-Path $ReportDirectory 'service-before-lifecycle.reg') /y | Out-Null
    & reg.exe export 'HKLM\Software\Classes\*\shell\HardwareScope.FileGuard' (Join-Path $ReportDirectory 'file-menu-before-lifecycle.reg') /y | Out-Null
    & reg.exe export 'HKLM\Software\Classes\Directory\shell\HardwareScope.FileGuard' (Join-Path $ReportDirectory 'directory-menu-before-lifecycle.reg') /y | Out-Null
    $tests = Join-Path $projectRoot 'artifacts\tests\fileguard-x64\HardwareScope.FileGuard.Tests.exe'
    $report = Join-Path $ReportDirectory 'fileguard-service-elevated.json'
    $code = Run $tests ('--service --report "' + $report + '"')
    Check 'elevated_broker_fixture_suite' ($code -eq 0) $code
    $testResults = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Check 'all_elevated_checks_pass' $testResults.passed $testResults
    $svc = Get-CimInstance Win32_Service -Filter "Name='HardwareScopeFileGuard'"
    $process = Get-Process -Id $svc.ProcessId
    $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 12
    $process.Refresh(); $timer.Stop()
    $idle = [pscustomobject]@{seconds=$timer.Elapsed.TotalSeconds;cpuMilliseconds=($process.TotalProcessorTime.TotalMilliseconds-$cpuBefore);cpuPercent=100*($process.TotalProcessorTime.TotalMilliseconds-$cpuBefore)/($timer.Elapsed.TotalMilliseconds*[Environment]::ProcessorCount);workingSetBytes=$process.WorkingSet64;privateBytes=$process.PrivateMemorySize64;handles=$process.HandleCount;mainWindowHandle=[long]$process.MainWindowHandle;sessionId=$process.SessionId}
    $idle | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'fileguard-service-idle.json') -Encoding utf8
    Check 'service_has_no_window' ($idle.mainWindowHandle -eq 0 -and $idle.sessionId -eq 0) $idle
    Check 'idle_has_negligible_cpu' ($idle.cpuMilliseconds -lt 100) $idle
    Check 'manual_start_configuration' ((Run $exe '/config manual') -eq 0) ''
    Check 'manual_start_saved' ((Get-Service HardwareScopeFileGuard).StartType -eq 'Manual') ''
    Check 'automatic_start_configuration' ((Run $exe '/config auto') -eq 0) ''
    $config = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\HardwareScopeFileGuard'
    Check 'delayed_automatic_saved' ($config.Start -eq 2 -and $config.DelayedAutostart -eq 1) $config.Start
    Check 'service_stop' ((Run $exe '/config stop') -eq 0 -and (Get-Service HardwareScopeFileGuard).Status -eq 'Stopped') ''
    Check 'service_start' ((Run $exe '/config start') -eq 0 -and (Get-Service HardwareScopeFileGuard).Status -eq 'Running') ''
    $uninstaller = Join-Path (Split-Path -Parent $exe) 'unins000.exe'
    Check 'uninstaller_exit' ((Run $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART') -eq 0) ''
    Check 'uninstall_removes_service' ($null -eq (Get-Service HardwareScopeFileGuard -ErrorAction SilentlyContinue)) ''
    Check 'uninstall_removes_file_menu' (-not (Test-Path -LiteralPath 'HKLM:\Software\Classes\*\shell\HardwareScope.FileGuard')) ''
    Check 'uninstall_removes_directory_menu' (-not (Test-Path -LiteralPath 'HKLM:\Software\Classes\Directory\shell\HardwareScope.FileGuard')) ''
    Check 'uninstall_removes_app_path' (-not (Test-Path -LiteralPath 'HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\HardwareScope.FileGuard.exe')) ''
    $installLog = Join-Path $ReportDirectory 'fileguard-after-uninstall.log'
    Check 'clean_reinstall_exit' ((Run $SetupPath ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG="' + $installLog + '"')) -eq 0) ''
    Check 'clean_reinstall_service_running' ((Get-Service HardwareScopeFileGuard).Status -eq 'Running') ''
    Check 'clean_reinstall_delayed_auto' ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\HardwareScopeFileGuard').DelayedAutostart -eq 1) ''
    Check 'installed_matches_built_binary' ((Get-FileHash $exe).Hash -eq (Get-FileHash (Join-Path $projectRoot 'artifacts\publish\fileguard-x64\HardwareScope.FileGuard.exe')).Hash) ''
    [pscustomobject]@{passed=$true;checks=$results} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'fileguard-install-lifecycle.json') -Encoding utf8
}
catch {
    [pscustomobject]@{passed=$false;error=$_.Exception.Message;checks=$results} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'fileguard-install-lifecycle.json') -Encoding utf8
    exit 1
}
