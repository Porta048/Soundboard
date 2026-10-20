param([string]$ActualFile = '')

$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testPath = Join-Path $workspacePath '.qa-audio'
if (-not ([IO.Path]::GetFullPath($testPath)).StartsWith($workspacePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Percorso test fuori dal progetto.' }
if (Test-Path -LiteralPath $testPath) { throw 'La cartella .qa-audio esiste gia: conservarla e usare un percorso pulito.' }
[IO.Directory]::CreateDirectory($testPath) | Out-Null
$decoder = Join-Path $workspacePath 'vendor\ffmpeg.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
try {
    & $decoder -nostdin -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=16x16:r=10:d=6' -f lavfi -i 'sine=frequency=1000:sample_rate=48000:duration=6' -c:v mpeg4 -c:a aac -shortest (Join-Path $testPath 'tone.mp4')
    if ($LASTEXITCODE -ne 0) { throw 'Generazione fixture MP4 non riuscita.' }
    & $decoder -nostdin -hide_banner -loglevel error -f lavfi -i 'anullsrc=r=48000:cl=stereo:d=3' (Join-Path $testPath 'silence.wav')
    if ($LASTEXITCODE -ne 0) { throw 'Generazione fixture silenziosa non riuscita.' }
    & $decoder -nostdin -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=16x16:r=10:d=1' -c:v mpeg4 -an (Join-Path $testPath 'video-only.mp4')
    if ($LASTEXITCODE -ne 0) { throw 'Generazione fixture senza audio non riuscita.' }
    $executable = Join-Path $testPath 'AudioRegression.exe'
    & $compiler /nologo /platform:x64 /target:exe /main:AudioRegression "/out:$executable" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll "/resource:$workspacePath\assets\soundboard-logo.ico,Soundboard.main.ico" "$workspacePath\src\Soundboard.cs" "$workspacePath\src\SoundboardTheme.cs" "$workspacePath\src\NativeAudio.cs" "$workspacePath\src\PcmAudioPlayer.cs" "$workspacePath\src\AudioRouting.cs" "$workspacePath\src\CoreAudioDevices.cs" "$workspacePath\src\ApoPcmOutput.cs" "$PSScriptRoot\AudioRegression.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione test audio non riuscita.' }
    & $executable $testPath $decoder $ActualFile
    if ($LASTEXITCODE -ne 0) { throw 'Test audio non riusciti.' }
}
finally { Remove-Item -LiteralPath $testPath -Recurse }
