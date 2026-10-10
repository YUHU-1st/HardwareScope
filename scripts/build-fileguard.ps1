param([string]$InnoCompiler, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
if (-not $InnoCompiler) {
    $InnoCompiler = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw '需要安装 Inno Setup 6，或使用 -InnoCompiler 指定 ISCC.exe。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -all -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '需要 Visual Studio 2022 C++ Build Tools 和 Windows SDK。' }
Push-Location -LiteralPath $projectRoot
try {
    & $msbuild src\HardwareScope.FileGuard\HardwareScope.FileGuard.vcxproj /nologo /v:minimal /p:Configuration=Release /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw '文件占用模块编译失败。' }
    if (-not $SkipTests) {
        & $msbuild tests\HardwareScope.FileGuard.Tests\HardwareScope.FileGuard.Tests.vcxproj /nologo /v:minimal /p:Configuration=Release /p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw '文件占用测试编译失败。' }
        New-Item -ItemType Directory -Force -Path artifacts\verification\1.3.0 | Out-Null
        & artifacts\tests\fileguard-x64\HardwareScope.FileGuard.Tests.exe --report "$projectRoot\artifacts\verification\1.3.0\fileguard-native.json"
        if ($LASTEXITCODE -ne 0) { throw '文件占用实机测试失败。' }
    }
    New-Item -ItemType Directory -Force -Path artifacts\release\1.3.0 | Out-Null
    & $InnoCompiler /Q scripts\fileguard-installer.iss
    if ($LASTEXITCODE -ne 0) { throw '文件占用独立安装包编译失败。' }
    Copy-Item -LiteralPath docs\文件占用模块.md -Destination artifacts\publish\fileguard-x64 -Force
    Copy-Item -LiteralPath docs\文件占用验证报告.md -Destination artifacts\publish\fileguard-x64 -Force
    Copy-Item -LiteralPath docs\界面更新验证.md -Destination artifacts\publish\fileguard-x64 -Force
    Compress-Archive -LiteralPath artifacts\publish\fileguard-x64\HardwareScope.FileGuard.exe,artifacts\publish\fileguard-x64\文件占用模块.md,artifacts\publish\fileguard-x64\文件占用验证报告.md,artifacts\publish\fileguard-x64\界面更新验证.md -DestinationPath artifacts\release\1.3.0\HardwareScope-FileGuard-1.1.0-windows-x64-portable.zip -Force
}
finally { Pop-Location }
