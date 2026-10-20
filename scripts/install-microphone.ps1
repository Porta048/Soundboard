param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\{0\.0\.1\.00000000\}\.\{[0-9A-Fa-f-]{36}\}$')]
    [string]$EndpointId,
    [string]$OwnerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
    [switch]$Uninstall,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

function Restore-EndpointProperties($RegistryRoot, $CapturePath, $Entries) {
    foreach ($entry in $Entries) {
        $key = $RegistryRoot.OpenSubKey($CapturePath + '\' + $entry.Group, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]::SetValue)
        if (-not $key) { throw 'Archivio proprieta del microfono non trovato durante il ripristino.' }
        try {
            if ($entry.Exists) {
                $kind = [Enum]::Parse([Microsoft.Win32.RegistryValueKind], [string]$entry.Kind)
                if ($kind -eq [Microsoft.Win32.RegistryValueKind]::MultiString) { $key.SetValue($entry.Name, [string[]]$entry.Value, $kind) }
                else { $key.SetValue($entry.Name, $entry.Value, $kind) }
            }
            else { $key.DeleteValue($entry.Name, $false) }
        }
        finally { $key.Dispose() }
    }
}

$registry = $null
try {
    if (-not [Environment]::Is64BitProcess) { throw 'Usare Windows PowerShell a 64 bit.' }
    $null = [Security.Principal.SecurityIdentifier]::new($OwnerSid)
    $endpointGuid = ([guid]$EndpointId.Substring($EndpointId.LastIndexOf('{'))).ToString('B')
    $classId = '{d1f6bc85-2934-4d97-a1d0-0c263b7766c2}'
    $capturePath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\' + $endpointGuid
    $installRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'SoundboardAudio'))
    $statePath = Join-Path $installRoot ($endpointGuid + '.xml')
    $classPath = 'SOFTWARE\Classes\CLSID\' + $classId
    $apoPath = 'SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\' + $classId
    # EFX slot used by earlier versions: Windows drops it when Voice Clarity is active, kept only to restore it
    $effectName = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'
    $modesName = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'
    # PKEY_CompositeFX_StreamEffectClsid and PKEY_SFX_ProcessingModes_Supported_For_Streaming
    $streamName = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13'
    $streamModesName = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},5'
    $modes = @('{c18e2f7e-933d-4965-b7d1-1eef228d2af3}','{98951333-b9cd-48b1-a0a3-ff40682d73f7}')
    $channelName = $classId + ',2'
    $ownerName = $classId + ',3'
    $disableName = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $capture = $registry.OpenSubKey($capturePath)
    if ($null -eq $capture) { throw 'Microfono non trovato. Selezionare un dispositivo attivo e riprovare.' }
    $capture.Dispose()
    $fx = $registry.OpenSubKey($capturePath + '\FxProperties')
    if (-not $fx) { throw 'Questo microfono non espone un archivio effetti compatibile con questo installatore di sviluppo.' }
    $fx.Dispose()

    if (-not $Uninstall) {
        $audioPolicy = $registry.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Audio')
        try { $developmentAudio = $audioPolicy -and $audioPolicy.GetValue('DisableProtectedAudioDG', 0) -eq 1 }
        finally { if ($audioPolicy) { $audioPolicy.Dispose() } }
        if (-not $developmentAudio) { throw 'La DLL e una build di sviluppo non firmata. Questo installatore non cambia la protezione audio di Windows: su questo PC serve un pacchetto APO firmato per il caricamento protetto.' }
        $sourceDll = Join-Path $PSScriptRoot 'SoundboardApo.dll'
        if (-not (Test-Path -LiteralPath $sourceDll)) { $sourceDll = Join-Path $PSScriptRoot '..\bin\native\SoundboardApo.dll' }
        if (-not (Test-Path -LiteralPath $sourceDll)) { throw 'DLL mancante. Eseguire scripts\build-native.ps1.' }
    }
    if ($CheckOnly) {
        'PASS: microfono trovato; archivio effetti disponibile; prerequisiti della build di sviluppo verificati.'
        'Non e stata modificata alcuna impostazione di Windows.'
        return
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Lanciare la configurazione dal menu Soundboard e accettare la richiesta amministratore di Windows.' }

    if ($Uninstall) {
        if (-not (Test-Path -LiteralPath $statePath)) { throw 'Backup della configurazione originale non trovato; nessuna proprieta rimossa.' }
        $state = Import-Clixml -LiteralPath $statePath
        if ($state.Endpoint -ne $endpointGuid) { throw 'Backup non corrispondente al microfono.' }
        Restore-EndpointProperties $registry $capturePath $state.Entries
        Remove-Item -LiteralPath $statePath
        if (@(Get-ChildItem -LiteralPath $installRoot -Filter '*.xml' -File).Count -eq 0) {
            $class = $registry.OpenSubKey($classPath + '\InprocServer32')
            try { $registeredDll = if ($class) { [string]$class.GetValue('') } else { '' } }
            finally { if ($class) { $class.Dispose() } }
            if ($registeredDll.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                $registry.DeleteSubKeyTree($classPath, $false)
                $registry.DeleteSubKeyTree($apoPath, $false)
            }
        }
        $message = 'Collegamento Soundboard rimosso dal microfono. Riapri le applicazioni audio; se necessario riavvia Windows. Le DLL restano archiviate per non eliminare file ancora caricati.'
    }
    else {
        [IO.Directory]::CreateDirectory($installRoot) | Out-Null
        $dllHash = (Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash.ToLowerInvariant()
        $versionDirectory = Join-Path $installRoot $dllHash.Substring(0, 16)
        [IO.Directory]::CreateDirectory($versionDirectory) | Out-Null
        $installedDll = Join-Path $versionDirectory 'SoundboardApo.dll'
        if (-not (Test-Path -LiteralPath $installedDll)) { Copy-Item -LiteralPath $sourceDll -Destination $installedDll }
        if ((Get-FileHash -LiteralPath $installedDll).Hash.ToLowerInvariant() -ne $dllHash) { throw 'Verifica della DLL installata fallita.' }
        # Backup keeps the original values; properties missing from an older backup are added before being changed
        $entries = @(if (Test-Path -LiteralPath $statePath) { (Import-Clixml -LiteralPath $statePath).Entries })
        foreach ($property in @(@('FxProperties',$effectName), @('FxProperties',$modesName), @('FxProperties',$streamName), @('FxProperties',$streamModesName), @('FxProperties',$channelName), @('FxProperties',$ownerName), @('Properties',$disableName))) {
            if (@($entries | Where-Object { $_.Group -eq $property[0] -and $_.Name -eq $property[1] }).Count -gt 0) { continue }
            $key = $registry.OpenSubKey($capturePath + '\' + $property[0])
            try {
                $exists = $key -and $key.GetValueNames() -contains $property[1]
                $entries += [pscustomobject]@{ Group=$property[0]; Name=$property[1]; Exists=[bool]$exists; Kind=if ($exists) { $key.GetValueKind($property[1]).ToString() } else { 'String' }; Value=if ($exists) { $key.GetValue($property[1]) } else { $null } }
            }
            finally { if ($key) { $key.Dispose() } }
        }
        [pscustomobject]@{ Endpoint=$endpointGuid; Entries=$entries } | Export-Clixml -LiteralPath $statePath
        $state = Import-Clixml -LiteralPath $statePath
        try {
            $key = $registry.CreateSubKey($classPath + '\InprocServer32')
            try { $key.SetValue('', $installedDll); $key.SetValue('ThreadingModel', 'Both') }
            finally { $key.Dispose() }
            $key = $registry.CreateSubKey($apoPath)
            try {
                $key.SetValue('FriendlyName', 'Soundboard microphone mixer')
                foreach ($pair in @{MajorVersion=1;MinorVersion=0;Flags=14;MinInputConnections=1;MaxInputConnections=1;MinOutputConnections=1;MaxOutputConnections=1;MaxInstances=-1;NumAPOInterfaces=1}.GetEnumerator()) { $key.SetValue($pair.Key, $pair.Value, [Microsoft.Win32.RegistryValueKind]::DWord) }
                $key.SetValue('APOInterface0', '{fd7f2b29-24d0-4b5c-b177-592c39f9ca10}')
                $key.DeleteValue('APOInterface1', $false)
                $key.DeleteValue('APOInterface2', $false)
            }
            finally { $key.Dispose() }
            Restore-EndpointProperties $registry $capturePath @($state.Entries | Where-Object { $_.Name -eq $effectName -or $_.Name -eq $modesName })
            $key = $registry.OpenSubKey($capturePath + '\FxProperties', [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]::QueryValues -bor [Security.AccessControl.RegistryRights]::SetValue)
            try {
                $key.SetValue($channelName, 'Global\Soundboard.Audio.v1.' + $endpointGuid + '.' + $OwnerSid)
                $key.SetValue($ownerName, $OwnerSid)
                # Stream effects of other vendors stay in the composite list, ours is appended
                $streams = @(@($key.GetValue($streamName)) | Where-Object { $_ -and $_ -ne $classId }) + $classId
                $streamModes = @(@($key.GetValue($streamModesName)) | Where-Object { $_ })
                foreach ($mode in $modes) { if ($streamModes -notcontains $mode) { $streamModes += $mode } }
                $key.SetValue($streamModesName, [string[]]$streamModes, [Microsoft.Win32.RegistryValueKind]::MultiString)
                $key.SetValue($streamName, [string[]]$streams, [Microsoft.Win32.RegistryValueKind]::MultiString)
            }
            finally { $key.Dispose() }
            # Effects are re-enabled only if explicitly disabled; a missing value is left missing as on the original device
            $key = $registry.OpenSubKey($capturePath + '\Properties', [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]::QueryValues -bor [Security.AccessControl.RegistryRights]::SetValue)
            try { if ($key.GetValue($disableName, 0) -ne 0) { $key.SetValue($disableName, 0, [Microsoft.Win32.RegistryValueKind]::DWord) } }
            finally { $key.Dispose() }
        }
        catch {
            $installError = $_.Exception.Message
            try { Restore-EndpointProperties $registry $capturePath $state.Entries }
            catch { throw ('Installazione fallita: ' + $installError + '. Ripristino da completare usando -Uninstall e il backup ' + $statePath) }
            throw ('Installazione fallita; proprieta del microfono ripristinate: ' + $installError)
        }
        $message = 'APO Soundboard registrato per il microfono selezionato. Riapri Discord e usa lo stesso microfono fisico. Se il collegamento non viene caricato, riavvia Windows. Il funzionamento va verificato con il test microfono.'
    }
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($message, 'Microfono Soundboard') | Out-Null
}
catch {
    if ($CheckOnly) { throw }
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Configurazione microfono non riuscita', 'OK', 'Error') | Out-Null
    exit 1
}
finally { if ($registry) { $registry.Dispose() } }
