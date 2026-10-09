param([string]$AndroidSdk = 'D:\DevSpace\android-sdk')
$ErrorActionPreference = 'Stop'
$androidRoot = $PSScriptRoot
if (-not (Test-Path -LiteralPath $AndroidSdk)) { throw 'Please specify an installed Android SDK using -AndroidSdk.' }
$env:ANDROID_HOME = $AndroidSdk
$env:ANDROID_SDK_ROOT = $AndroidSdk
$signingDir = Join-Path $androidRoot 'signing'
$signingProperties = Join-Path $signingDir 'release.properties'
$keystore = Join-Path $signingDir 'hardwarescope-release.jks'
if (-not (Test-Path -LiteralPath $signingProperties)) {
    if (Test-Path -LiteralPath $keystore) { throw 'Signing key already exists. Restore its release.properties; the key must not be replaced.' }
    New-Item -ItemType Directory -Force -Path $signingDir | Out-Null
    $random = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Fill($random)
    $password = [Convert]::ToBase64String($random)
    $passwordFile = Join-Path $signingDir 'password.tmp'
    try {
        Set-Content -LiteralPath $passwordFile -Value $password -Encoding ascii
        & keytool -genkeypair -keystore $keystore -alias hardwarescope -storepass:file $passwordFile -keypass:file $passwordFile -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=HardwareScope Release, OU=Software, O=HardwareScope, C=CN'
        if ($LASTEXITCODE -ne 0) { throw 'Signing key generation failed.' }
        @("storeFile=signing/hardwarescope-release.jks", "storePassword=$password", "keyAlias=hardwarescope", "keyPassword=$password") | Set-Content -LiteralPath $signingProperties -Encoding ascii
    } finally { if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile } }
}
$wrapper = Join-Path $androidRoot 'gradlew.bat'
$localGradle = Join-Path $androidRoot '.tools\gradle-8.9\bin\gradle.bat'
$gradleCommand = if (Test-Path -LiteralPath $wrapper) { $wrapper } elseif (Test-Path -LiteralPath $localGradle) { $localGradle } else { 'gradle' }
$buildRoot = $androidRoot
$temporaryDrive = $null
try {
    # JDK 17 on Chinese Windows decodes Gradle test argument files using the
    # system code page. A temporary ASCII drive alias avoids corrupting paths.
    if ($androidRoot -match '[^\x00-\x7F]') {
        $temporaryDrive = 90..72 | ForEach-Object { ([char]$_).ToString() + ':' } | Where-Object { -not (Test-Path ($_ + '\')) } | Select-Object -First 1
        if (-not $temporaryDrive) { throw 'No free drive letter is available for the Android build.' }
        & subst.exe $temporaryDrive $androidRoot
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the temporary Android build drive.' }
        $buildRoot = $temporaryDrive + '\'
    }
    & $gradleCommand -p $buildRoot assembleRelease testReleaseUnitTest lintRelease
    if ($LASTEXITCODE -ne 0) { throw 'Android build, unit tests or lint failed.' }
} finally {
    if ($temporaryDrive -and $buildRoot -ne $androidRoot) { & subst.exe $temporaryDrive /D }
}
$buildTools = Get-ChildItem -LiteralPath (Join-Path $AndroidSdk 'build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$apk = Join-Path $androidRoot 'app\build\outputs\apk\release\app-release.apk'
& (Join-Path $buildTools.FullName 'apksigner.bat') verify --verbose --print-certs $apk
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
Get-FileHash -Algorithm SHA256 -LiteralPath $apk
