$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testPath = [IO.Path]::GetFullPath((Join-Path $workspacePath '.qa-microphone'))
if (-not $testPath.StartsWith($workspacePath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Percorso test fuori dal progetto.' }
if (Test-Path -LiteralPath $testPath) { throw 'La cartella .qa-microphone esiste gia; conservarla e usare un percorso pulito.' }
& (Join-Path $workspacePath 'scripts\build-native.ps1') -Tests
[IO.Directory]::CreateDirectory($testPath) | Out-Null
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $executable = Join-Path $testPath 'MicrophoneBridgeRegression.exe'
    & $compiler /nologo /platform:x64 /target:exe /main:MicrophoneBridgeRegression "/out:$executable" "$workspacePath\src\NativeAudio.cs" "$workspacePath\src\PcmAudioPlayer.cs" "$workspacePath\src\ApoPcmOutput.cs" "$PSScriptRoot\MicrophoneBridgeRegression.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione test microfono non riuscita.' }
    & $executable $testPath "$workspacePath\bin\native\ApoRegression.exe" "$workspacePath\bin\native\SoundboardApo.dll" "$workspacePath\vendor\ffmpeg.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Verifica del collegamento player-APO non riuscita.' }
}
finally { Remove-Item -LiteralPath $testPath -Recurse }
