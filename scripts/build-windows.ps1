param([string]$InnoCompiler)
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
Push-Location -LiteralPath $projectRoot
try {
    & "$PSScriptRoot\build-fileguard.ps1" -InnoCompiler $InnoCompiler
    dotnet build HardwareScope.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows 构建失败。' }
    dotnet run --project tests\HardwareScope.Smoke -c Release --no-build -- artifacts\verification\1.2.0
    if ($LASTEXITCODE -ne 0) { throw '实机与 API 验证失败。' }
    dotnet publish src\HardwareScope.Desktop\HardwareScope.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o artifacts\publish\windows-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows 发布失败。' }
    $publishPath = Join-Path $projectRoot 'artifacts\publish\windows-x64'
    $appearanceReport = Join-Path $projectRoot 'artifacts\verification\1.2.0\appearance.json'
    $appearance = Start-Process -FilePath "$publishPath\HardwareScope.exe" -ArgumentList ('--verify-themes="' + $appearanceReport + '"') -WorkingDirectory $publishPath -WindowStyle Hidden -Wait -PassThru
    if ($appearance.ExitCode -ne 0) { throw "发布程序主题验证失败，错误码 $($appearance.ExitCode)。" }
    New-Item -ItemType Directory -Force -Path "$publishPath\docs","$publishPath\scripts","$publishPath\licenses","$publishPath\android","$publishPath\FileGuard",'artifacts\release\1.2.0' | Out-Null
    Get-ChildItem -LiteralPath "$publishPath\FileGuard" -Filter 'HardwareScope-FileGuard-*-windows-x64-setup.exe' -File | Where-Object Name -ne 'HardwareScope-FileGuard-1.1.0-windows-x64-setup.exe' | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Copy-Item -LiteralPath artifacts\publish\fileguard-x64\HardwareScope.FileGuard.exe,artifacts\release\1.2.0\HardwareScope-FileGuard-1.1.0-windows-x64-setup.exe -Destination "$publishPath\FileGuard" -Force
    Copy-Item -LiteralPath README.md,THIRD_PARTY_NOTICES.md -Destination $publishPath -Force
    Copy-Item docs\*.md "$publishPath\docs" -Force
    Copy-Item -LiteralPath docs\images -Destination "$publishPath\docs" -Recurse -Force
    Copy-Item -LiteralPath android\README.md,android\VALIDATION.md -Destination "$publishPath\android" -Force
    Copy-Item -LiteralPath scripts\Enable-LanFirewall.ps1 -Destination "$publishPath\scripts" -Force
    $assets = Get-Content src\HardwareScope.Desktop\obj\project.assets.json -Raw | ConvertFrom-Json -AsHashtable
    $dependencies = foreach ($library in $assets.libraries.GetEnumerator()) {
        if ($library.Value.type -ne 'package') { continue }
        $id,$version = $library.Key -split '/',2
        $packagePath = Join-Path "$env:USERPROFILE\.nuget\packages" ($id.ToLowerInvariant() + '\' + $version)
        $nuspec = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' | Select-Object -First 1
        [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
        $license = $metadata.package.metadata.license
        if ($license.type -eq 'file') {
            $licensePath = Join-Path $packagePath $license.'#text'
            Copy-Item -LiteralPath $licensePath -Destination (Join-Path "$publishPath\licenses" ($id + '-' + $version + '-LICENSE.txt')) -Force
        }
        [pscustomobject]@{id=$id;version=$version;license=$license.'#text';licenseType=$license.type;projectUrl=$metadata.package.metadata.projectUrl;repository=$metadata.package.metadata.repository.url}
    }
    $dependencies | ConvertTo-Json -Depth 5 | Set-Content "$publishPath\licenses\dependencies.json" -Encoding utf8
    Copy-Item -LiteralPath licenses\Mono-Posix-LICENSE.txt -Destination "$publishPath\licenses" -Force
    foreach ($expression in @('MIT','MPL-2.0','Apache-2.0','BSD-2-Clause','BSD-3-Clause')) {
        $licensePath = "$publishPath\licenses\$expression.txt"
        if (-not (Test-Path -LiteralPath $licensePath)) {
            Invoke-WebRequest ("https://raw.githubusercontent.com/spdx/license-list-data/main/text/" + $expression + '.txt') -OutFile $licensePath
        }
    }
    & $InnoCompiler /Q scripts\installer.iss
    if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败。' }
    Compress-Archive -Path "$publishPath\*" -DestinationPath artifacts\release\1.2.0\HardwareScope-1.2.0-windows-x64-portable.zip -Force
    Get-ChildItem artifacts\release\1.2.0 -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name } | Set-Content artifacts\release\1.2.0\SHA256SUMS.txt -Encoding utf8
    Write-Host 'Windows 安装包与免安装包已生成。'
}
finally { Pop-Location }
