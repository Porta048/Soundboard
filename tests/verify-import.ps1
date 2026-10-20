param([string]$ExecutablePath = "$PSScriptRoot\..\Soundboard.exe")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testPath = Join-Path $workspacePath '.qa-import'
if (-not ([IO.Path]::GetFullPath($testPath)).StartsWith($workspacePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Percorso test fuori dal progetto.' }
if (Test-Path -LiteralPath $testPath) { throw 'La cartella .qa-import esiste gia: conservarla e usare un percorso pulito.' }
[IO.Directory]::CreateDirectory($testPath) | Out-Null
$sourceList = Join-Path $env:APPDATA 'Leppsoft\soundlist.spl'
$sourceStats = Join-Path $env:APPDATA 'Leppsoft\stats.spl'
$beforeList = (Get-FileHash -LiteralPath $sourceList).Hash
$beforeStats = (Get-FileHash -LiteralPath $sourceStats).Hash
$assembly = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($ExecutablePath))
$windowType = $assembly.GetType('SoundboardWindow')
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$form = [Activator]::CreateInstance($windowType, $true)
$reloaded = $null

try {
    $storagePath = Join-Path $testPath 'library.xml'
    $windowType.GetField('libraryPath', $flags).SetValue($form, $storagePath)
    $sounds = $windowType.GetField('sounds', $flags).GetValue($form)
    $sounds.Clear()
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $import = $windowType.GetMethod('ImportSoundpadFile', $flags)
    $added = $import.Invoke($form, @([string]$sourceList))
    if ($added -ne 1 -or $sounds.Count -ne 1) { throw 'Importazione della lista reale non riuscita.' }
    $sound = $sounds[0]
    if ($sound.Name -ne 'dictator-mbappe-meme' -or $sound.Duration -ne '0:10' -or $sound.PlayCount -ne 5) { throw 'Titolo, durata o statistiche importati non corretti.' }
    if ($import.Invoke($form, @([string]$sourceList)) -ne 0 -or $sounds.Count -ne 1) { throw 'La seconda importazione ha duplicato il suono.' }
    $missing = [Activator]::CreateInstance($assembly.GetType('SoundEntry'), $true)
    $missing.Path = Join-Path $testPath 'missing.mp3'
    $windowType.GetMethod('PlaySound', $flags).Invoke($form, @($missing))
    $status = $windowType.GetField('status', $flags).GetValue($form)
    if ($missing.PlayCount -ne 0 -or $status.Text -notlike 'File non trovato*') { throw 'Gestione del file mancante non corretta.' }

    $fixturePath = Join-Path $testPath 'fixture.spl'
    [IO.File]::WriteAllText($fixturePath, '<Soundlist><Sound url="https://example.invalid/audio.mp3"/><Sound url="relative.mp3" title="Relativo"/><Sound url="relative.mp3" title="Duplicato"/></Soundlist>')
    if ($import.Invoke($form, @([string]$fixturePath)) -ne 1) { throw 'Percorsi locali, URL o duplicati gestiti in modo errato.' }
    if ($sounds[1].Path -ne (Join-Path $testPath 'relative.mp3')) { throw 'Risoluzione del percorso relativo non corretta.' }
    [IO.File]::WriteAllText($fixturePath, '<!DOCTYPE Soundlist [<!ENTITY test SYSTEM "file:///missing">]><Soundlist><Sound url="&test;"/></Soundlist>')
    $blocked = $false
    try { $import.Invoke($form, @([string]$fixturePath)) | Out-Null }
    catch { $blocked = $true }
    if (-not $blocked -or $sounds.Count -ne 2) { throw 'Il documento con DTD non e stato rifiutato senza modifiche.' }

    $tonePath = Join-Path $testPath 'silence.wav'
    $writer = New-Object IO.BinaryWriter([IO.File]::Create($tonePath))
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([int]264636)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
        $writer.Write([int]16)
        $writer.Write([uint16]1)
        $writer.Write([uint16]1)
        $writer.Write([int]44100)
        $writer.Write([int]88200)
        $writer.Write([uint16]2)
        $writer.Write([uint16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([int]264600)
        $writer.Write((New-Object byte[] 264600))
    }
    finally { $writer.Dispose() }
    $tone = [Activator]::CreateInstance($assembly.GetType('SoundEntry'), $true)
    $tone.Path = $tonePath
    $sounds.Add($tone)
    $player = $windowType.GetField('player', $flags).GetValue($form)
    $player.CacheDirectory = Join-Path $testPath 'cache'
    $player.Volume = 0
    $windowType.GetMethod('PlaySound', $flags).Invoke($form, @($tone))
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($tone.PlayCount -eq 0 -and $watch.ElapsedMilliseconds -lt 4000) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    if ($tone.PlayCount -ne 1 -or $tone.Duration -ne '0:03') { throw 'Apertura audio, durata o conteggio non verificati.' }
    $windowType.GetMethod('PausePlayback', $flags).Invoke($form, @($form, [EventArgs]::Empty))
    $seek = $windowType.GetField('seek', $flags).GetValue($form)
    $seek.Value = 500
    if ([Math]::Abs($player.Position.TotalSeconds - 1.5) -gt 0.2) { throw 'Posizione di riproduzione non coerente.' }
    $windowType.GetMethod('StopPlayback', $flags).Invoke($form, @($form, [EventArgs]::Empty))
    $form.Close()

    $reloaded = [Activator]::CreateInstance($windowType, $true)
    $windowType.GetField('libraryPath', $flags).SetValue($reloaded, $storagePath)
    $restored = $windowType.GetField('sounds', $flags).GetValue($reloaded)
    $restored.Clear()
    $windowType.GetMethod('LoadLibrary', $flags).Invoke($reloaded, @())
    if ($restored.Count -ne 3 -or $restored[0].PlayCount -ne 5 -or $restored[0].Duration -ne '0:10' -or $restored[2].PlayCount -ne 1) { throw 'Metadati non conservati al riavvio.' }
    if ((Get-FileHash -LiteralPath $sourceList).Hash -ne $beforeList -or (Get-FileHash -LiteralPath $sourceStats).Hash -ne $beforeStats) { throw 'I file originali Soundpad sono cambiati durante il test.' }
    'PASS: lista reale, statistiche, duplicati, file mancante, percorsi relativi, URL esclusi, DTD, audio silenzioso, seek e persistenza.'
}
finally {
    if (-not $form.IsDisposed) { $form.Close() }
    $form.Dispose()
    if ($null -ne $reloaded) { $reloaded.Dispose() }
    Remove-Item -LiteralPath $testPath -Recurse
}
