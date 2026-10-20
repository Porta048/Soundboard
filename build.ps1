param([string]$OutputPath = "$PSScriptRoot\Soundboard.exe")

$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath "$PSScriptRoot\vendor\ffmpeg.exe")) { throw 'Manca vendor\ffmpeg.exe.' }
$resources = @("/resource:$PSScriptRoot\assets\soundboard-logo.ico,Soundboard.main.ico")
& $compilerPath /nologo /target:winexe /out:"$OutputPath" /win32icon:"$PSScriptRoot\assets\soundboard-logo.ico" $resources /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll "$PSScriptRoot\Soundboard.cs" "$PSScriptRoot\SoundboardTheme.cs" "$PSScriptRoot\NativeAudio.cs" "$PSScriptRoot\PcmAudioPlayer.cs" "$PSScriptRoot\AudioRouting.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilazione non riuscita.' }
