param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$')]
    [string]$RepositoryName = 'Soundboard',
    [switch]$Private
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Join-Path $PSScriptRoot '..')
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git non e installato.' }

if (-not (Test-Path -LiteralPath '.git')) {
    git init -b main
    if ($LASTEXITCODE -ne 0) { throw 'Inizializzazione Git non riuscita.' }
}
$branch = git branch --show-current
if ($LASTEXITCODE -ne 0 -or $branch -ne 'main') { throw 'Eseguire lo script sul branch main del progetto.' }
git add -- .
if ($LASTEXITCODE -ne 0) { throw 'Scrittura indice Git non riuscita. Eseguire in un normale PowerShell di Windows.' }
git diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Correggere gli errori indicati da git diff --check.' }
git diff --cached --quiet
if ($LASTEXITCODE -eq 1) {
    git commit -m 'Prepare Soundboard source repository'
    if ($LASTEXITCODE -ne 0) { throw 'Commit non riuscito. Verificare nome ed email configurati in Git.' }
}
elseif ($LASTEXITCODE -ne 0) { throw 'Verifica delle modifiche Git non riuscita.' }

# Le credenziali restano in memoria e nel gestore Git configurato.
$credentialLines = "protocol=https`nhost=github.com`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw 'Accesso GitHub non disponibile. Accedere tramite Git Credential Manager e riprovare.' }
$credentialFields = @{}
foreach ($line in $credentialLines) {
    $parts = $line -split '=', 2
    if ($parts.Length -eq 2) { $credentialFields[$parts[0]] = $parts[1] }
}
if (-not $credentialFields.ContainsKey('password')) { throw 'Git non ha restituito una credenziale GitHub utilizzabile.' }
$headers = @{
    Authorization = 'Bearer ' + $credentialFields['password']
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'Soundboard-repository-setup'
    'X-GitHub-Api-Version' = '2022-11-28'
}
try {
    $account = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers
    $remoteUrl = 'https://github.com/' + $account.login + '/' + $RepositoryName + '.git'
    $existingRemote = $null
    $remotes = @(git remote)
    if ($LASTEXITCODE -ne 0) { throw 'Lettura remoti Git non riuscita.' }
    if ($remotes -contains 'origin') {
        $existingRemote = git remote get-url origin
        if ($LASTEXITCODE -ne 0) { throw 'Lettura origin non riuscita.' }
        if ($existingRemote -ne $remoteUrl) { throw 'Origin punta a un altro repository: nessuna modifica eseguita sul remoto.' }
    }

    $repository = $null
    try {
        $repository = Invoke-RestMethod -Uri ('https://api.github.com/repos/' + $account.login + '/' + $RepositoryName) -Headers $headers
    }
    catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw 'Impossibile verificare il repository GitHub. Controllare connessione e autorizzazioni.' }
    }
    if ($null -eq $repository) {
        $body = @{ name = $RepositoryName; private = $Private.IsPresent; description = 'Windows soundboard in C# and Windows Forms'; auto_init = $false } | ConvertTo-Json
        try {
            $repository = Invoke-RestMethod -Method Post -Uri 'https://api.github.com/user/repos' -Headers $headers -ContentType 'application/json' -Body $body
        }
        catch { throw 'Creazione GitHub non riuscita. Verificare che la credenziale possa creare repository e che il nome sia disponibile.' }
    }
    elseif ([bool]$repository.private -ne $Private.IsPresent) { throw 'Il repository esiste con una visibilita diversa da quella richiesta. Nessun push eseguito.' }

    if ([string]::IsNullOrWhiteSpace($existingRemote)) {
        git remote add origin $remoteUrl
        if ($LASTEXITCODE -ne 0) { throw 'Configurazione origin non riuscita.' }
    }
    git push -u origin main
    if ($LASTEXITCODE -ne 0) { throw 'Push non riuscito. I commit locali sono conservati; nessun push forzato eseguito.' }
    Write-Host ('Repository pubblicato: ' + $repository.html_url)
}
finally {
    $headers.Clear()
    $credentialFields.Clear()
    $credentialLines = $null
}
