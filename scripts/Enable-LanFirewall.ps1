param([Parameter(Mandatory=$true)][string]$ExecutablePath, [int]$Port = 47831)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行 PowerShell，再执行此脚本。'
}
$resolved = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($resolved) -ne 'HardwareScope.exe') { throw '请选择 HardwareScope.exe。' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw '端口必须在 1024–65535 之间。' }
$existing = Get-NetFirewallRule -Name 'HardwareScope-LAN' -ErrorAction SilentlyContinue
if ($existing) { throw 'HardwareScope-LAN 规则已存在，请在 Windows 防火墙中检查现有规则。' }
New-NetFirewallRule -Name 'HardwareScope-LAN' -DisplayName 'HardwareScope 局域网连接' -Direction Inbound -Action Allow -Program $resolved -Protocol TCP -LocalPort $Port -RemoteAddress LocalSubnet -Profile Private | Out-Null
Write-Host '已允许专用网络中的局域网手机连接。电脑端仍需启用连接并授权控制。'
