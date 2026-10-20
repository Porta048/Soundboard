param([string]$OutputPath = "$PSScriptRoot\..\bin\Soundboard.exe")

$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath "$workspacePath\vendor\ffmpeg.exe")) { throw 'Manca vendor\ffmpeg.exe.' }
& (Join-Path $PSScriptRoot 'build-native.ps1')
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$resources = @("/resource:$workspacePath\assets\soundboard-logo.ico,Soundboard.main.ico")
& $compilerPath /nologo /platform:x64 /target:winexe /out:"$OutputPath" /win32icon:"$workspacePath\assets\soundboard-logo.ico" $resources /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll "$workspacePath\src\Soundboard.cs" "$workspacePath\src\SoundboardTheme.cs" "$workspacePath\src\NativeAudio.cs" "$workspacePath\src\PcmAudioPlayer.cs" "$workspacePath\src\AudioRouting.cs" "$workspacePath\src\CoreAudioDevices.cs" "$workspacePath\src\ApoPcmOutput.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilazione non riuscita.' }
$runtimeDirectory = [IO.Path]::GetDirectoryName($OutputPath)
if (-not [string]::Equals($runtimeDirectory, "$workspacePath\bin", [StringComparison]::OrdinalIgnoreCase)) {
    $runtimeNative = Join-Path $runtimeDirectory 'native'
    [IO.Directory]::CreateDirectory($runtimeNative) | Out-Null
    Copy-Item -LiteralPath "$workspacePath\bin\native\SoundboardApo.dll", "$workspacePath\bin\native\install-microphone.ps1" -Destination $runtimeNative
}
$runtimeVendor = Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) 'vendor'
if (-not [string]::Equals([IO.Path]::GetFullPath($runtimeVendor), "$workspacePath\vendor", [StringComparison]::OrdinalIgnoreCase)) {
    [IO.Directory]::CreateDirectory($runtimeVendor) | Out-Null
    Copy-Item -LiteralPath "$workspacePath\vendor\ffmpeg.exe", "$workspacePath\vendor\FFmpeg-LICENSE.txt", "$workspacePath\vendor\FFmpeg-README.txt", "$workspacePath\vendor\NOTICE.md" -Destination $runtimeVendor
}
