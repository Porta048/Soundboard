# Soundboard

Applicazione Windows in C# e Windows Forms per organizzare e riprodurre file audio, con interfaccia grigio scuro e comandi blu.

## Funzioni

- Aggiunta di file audio e video; importazione di liste Soundpad `.spl` e statistiche compatibili.
- Riproduzione, pausa, stop, ricerca nella traccia e volume applicato ai campioni audio.
- Estrazione dell'audio da MP4, M4A, WAV, MP3, WMA, AAC, FLAC, OGG, Opus, MKV e WebM tramite FFmpeg.
- Recupero dei file mancanti e salvataggio della libreria e delle preferenze.
- Scorciatoie globali: `Ctrl+Alt+1` fino a `Ctrl+Alt+9` e `Ctrl+Alt+F12` per lo stop.
- Uscita sugli altoparlanti, su un dispositivo virtuale compatibile gia installato o su entrambi.

**Stato del microfono:** l'inserimento diretto dei suoni nel microfono fisico, mantenendo lo stesso ingresso in Discord, non e ancora implementato. La modalita microfono attuale invia l'audio a un dispositivo virtuale e richiede che il relativo percorso sia configurato. Non installa driver, APO o mixer.

`CoreAudioDevices.cs` contiene il rilevamento dei dispositivi di acquisizione e del microfono predefinito per le comunicazioni. E stato verificato separatamente; non e ancora integrato nel player.

## Compilazione

Requisiti:

- Windows a 64 bit con .NET Framework 4.x e il relativo compilatore C#.
- Una distribuzione Windows di FFmpeg, scaricata dal [sito ufficiale](https://ffmpeg.org/download.html), con `ffmpeg.exe` copiato nella cartella `vendor`.

Da PowerShell nella cartella del progetto:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Avviare `Soundboard.exe`. Conservare `vendor\ffmpeg.exe` accanto all'eseguibile nella stessa struttura di cartelle. I binari compilati e il decoder non sono inclusi nel repository.

Per generare il logo originale:

```powershell
powershell -ExecutionPolicy Bypass -File .\generate-logo.ps1
```

## Dati locali

La libreria, le impostazioni e la cache del decoder sono salvate in `%LOCALAPPDATA%\Soundboard`. Questi dati e i file audio personali non fanno parte del repository. I file temporanei del decoder vengono rimossi a stop, annullamento o errore.

## Verifiche

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\verify-audio.ps1
powershell -ExecutionPolicy Bypass -File .\tests\verify-import.ps1
```

I test esistenti verificano riproduzione, guadagno, silenzio, pausa, ricerca, annullamento, persistenza e importazione. Sono test di integrazione legati alla macchina: il test audio attuale richiede un'uscita Realtek e un ingresso Voicemeeter; il test di importazione richiede una lista Soundpad locale. Non verificano la ricezione audio da parte di altri utenti Discord.

## Dipendenze e risorse

L'interfaccia e il logo sono implementati nel progetto. La build non incorpora immagini o DLL di Soundpad. I file estratti e le evidenze dell'analisi locale sono esclusi dal repository.

FFmpeg e eseguito come processo separato. Vedere [vendor/NOTICE.md](vendor/NOTICE.md) per provenienza, licenza e sorgenti della distribuzione locale. Soundboard e un progetto indipendente e non e affiliato a Soundpad o Leppsoft.

## Pubblicazione su GitHub

Da un normale PowerShell di Windows, con accesso GitHub tramite il gestore di credenziali Git:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-github.ps1
```

Lo script crea il commit locale, crea una repository privata `Soundboard` nell'account autenticato e carica il branch `main`. Per scegliere il nome usare `-RepositoryName Nome`; per la visibilita pubblica aggiungere `-Public`. Non esegue push forzati e non salva credenziali nei file del progetto o nell'URL del remoto.
