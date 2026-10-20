# Soundboard

[English](README.en.md) | **Italiano**

Applicazione Windows in C# e Windows Forms per organizzare e riprodurre file audio, con interfaccia grigio scuro e comandi blu. I suoni possono essere inviati come microfono a Discord e ad altre app di chiamata tramite **Voicemeeter**.

![Finestra principale di Soundboard](assets/soundboard-screenshot.png)

## Funzioni

- Aggiunta di file audio e video; importazione di liste Soundpad `.spl` e statistiche compatibili.
- Riproduzione, pausa, stop, ricerca nella traccia e volume applicato ai campioni audio.
- Estrazione dell'audio da MP4, M4A, WAV, MP3, WMA, AAC, FLAC, OGG, Opus, MKV e WebM tramite FFmpeg.
- Recupero dei file mancanti e salvataggio della libreria e delle preferenze.
- Scorciatoie globali: `Ctrl+Alt+1` fino a `Ctrl+Alt+9` e `Ctrl+Alt+F12` per lo stop.
- Uscita sugli altoparlanti, sul microfono virtuale di Voicemeeter o su entrambi.

## Usare i suoni in Discord con Voicemeeter

Soundboard suona i file sull'ingresso virtuale di Voicemeeter. Voicemeeter li miscela con il microfono reale e li manda a Discord su un'uscita virtuale, che Discord usa come microfono. Le persone in chiamata sentono sia la voce sia i suoni.

```text
Microfono reale ---> HARDWARE INPUT 1 --(B)--> Voicemeeter Out B1 ---> Discord (input)
Soundboard -------> VIRTUAL INPUT ----(B)--/
                                      --(A)--> HARDWARE OUT A1 ---> cuffie/casse
```

Configurazione verificata con Voicemeeter (versione standard) di [VB-Audio](https://vb-audio.com/Voicemeeter/), Discord e un microfono USB su Windows 11.

### 1. Voicemeeter

1. Installare Voicemeeter e riavviare Windows se richiesto.
2. **HARDWARE INPUT 1**: scegliere il proprio microfono, preferibilmente nella voce `WDM`. Attivare **B**, disattivare **A** per non sentire la propria voce.
3. **VIRTUAL INPUT** (Voicemeeter VAIO): attivare **B**. Attivare anche **A** per sentire i suoni in cuffia o sulle casse.
4. **HARDWARE OUT A1**: scegliere cuffie o casse nella voce **`MME`**. Con `WDM` Voicemeeter puo occupare il dispositivo in modo esclusivo: Discord e le altre app non riescono piu a suonarci e non si sentono le altre persone.
5. Menu > **Run on Windows Startup**, cosi Voicemeeter parte con Windows. Deve essere aperto mentre si usa Soundboard.

### 2. Soundboard

1. Menu **Riproduci**: togliere la spunta da **Usa microfono fisico (APO Soundboard)**.
2. **Riproduci > Dispositivo microfono virtuale**: scegliere **Voicemeeter Input**.
3. Riprodurre con il pulsante microfono (solo microfono) oppure con **Riproduci file su altoparlanti e microfono**.

### 3. Discord

Impostazioni > **Voce e video**:

- **Dispositivo di input**: `Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)`.
- **Dispositivo di output**: cuffie o casse (oppure `Predefinito`).
- Disattivare la **soppressione del rumore**: tratta i suoni come rumore di fondo e li taglia.

Con il test microfono di Discord si devono sentire sia la voce sia il file riprodotto.

### Problemi frequenti

| Sintomo | Causa | Soluzione |
| --- | --- | --- |
| Si sentono i suoni ma non la propria voce | Il microfono non e assegnato a HARDWARE INPUT 1 | Riselezionare il microfono su HARDWARE INPUT 1 e verificare che **B** sia attivo |
| Non si sentono le altre persone in Discord | HARDWARE OUT A1 in `WDM` occupa le casse in modo esclusivo | Selezionare cuffie o casse nella voce `MME` |
| I suoni si sentono a tratti o non arrivano | Soppressione del rumore di Discord | Disattivarla in Voce e video |
| Nessun suono in Discord | Voicemeeter chiuso o input Discord errato | Avviare Voicemeeter e scegliere `Voicemeeter Out B1` come input |

## Microfono fisico tramite APO (sperimentale)

Il progetto contiene anche un percorso senza Voicemeeter: un APO nativo (`native/`) che somma il file al flusso del microfono fisico. Sul PC di prova Windows 11 carica l'APO di un altro produttore, firmato, nello stesso punto della catena audio, ma **scarta la nostra DLL perche non e firmata**: il microfono non si apre piu in modalita condivisa. Il percorso resta quindi sperimentale e richiede una DLL con firma digitale valida. Dettagli, installazione e ripristino in [docs/MICROPHONE.md](docs/MICROPHONE.md).

## Struttura del progetto

```text
Soundboard/
  src/          Sorgenti C# dell'applicazione
  native/       APO C++ sperimentale e protocollo di trasferimento audio
  scripts/      Compilazione, installazione APO, generazione logo e pubblicazione GitHub
  assets/       Logo e screenshot dell'applicazione
  tests/        Test di integrazione audio, importazione e APO
  vendor/       Decoder locale, licenze e note della dipendenza
  bin/          Applicazione compilata con dipendenze pronte all'avvio
  docs/         Documentazione tecnica
  research/     Evidenze, strumenti e risorse dell'analisi locale
```

`bin`, le evidenze in `research` e le note locali in `docs` sono esclusi da Git. I sorgenti, gli script, i test, il logo, lo screenshot e le note della dipendenza sono versionati.

## Compilazione

Requisiti:

- Windows a 64 bit con .NET Framework 4.x e il relativo compilatore C#.
- Visual Studio Build Tools con compilatore C++ e Windows SDK, per compilare l'APO nativo.
- Una distribuzione Windows di FFmpeg, scaricata dal [sito ufficiale](https://ffmpeg.org/download.html), con `ffmpeg.exe` copiato nella cartella `vendor`.

Da PowerShell nella cartella del progetto:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

Avviare `bin\Soundboard.exe`. La compilazione copia il decoder e le relative note in `bin\vendor`: per spostare l'applicazione copiare l'intera cartella `bin`. I binari compilati e il decoder non sono inclusi nel repository.

Per generare il logo originale:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\generate-logo.ps1
```

## Dati locali

La libreria, le impostazioni e la cache del decoder sono salvate in `%LOCALAPPDATA%\Soundboard`. Questi dati e i file audio personali non fanno parte del repository. I file temporanei del decoder vengono rimossi a stop, annullamento o errore.

## Verifiche

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\verify-audio.ps1
powershell -ExecutionPolicy Bypass -File .\tests\verify-import.ps1
powershell -ExecutionPolicy Bypass -File .\tests\verify-microphone.ps1
```

I test verificano riproduzione, guadagno, silenzio, pausa, ricerca, annullamento, persistenza e importazione. Il test audio richiede un'uscita Realtek e un ingresso Voicemeeter; il test di importazione genera una lista e statistiche di prova senza leggere o modificare la libreria Soundpad personale. I dati temporanei restano nel progetto e vengono rimossi al termine.

`verify-microphone.ps1` compila la DLL dell'APO e la prova in un host COM isolato con audio sintetico, inclusa la creazione aggregata usata dal motore audio di Windows. Non apre il microfono reale, non installa componenti e non invia audio a Discord.

Il percorso Voicemeeter e stato verificato a mano: un tono riprodotto dal player di Soundboard su `Voicemeeter Input` arriva su `Voicemeeter Out B1` con lo stesso livello del file, e in Discord si sentono voce, suoni e le altre persone.

## Dipendenze e risorse

L'interfaccia e il logo sono implementati nel progetto. La build non incorpora immagini o DLL di Soundpad. I file estratti e le evidenze dell'analisi locale sono esclusi dal repository. Voicemeeter e un prodotto di VB-Audio e va installato separatamente.

FFmpeg e eseguito come processo separato. Vedere [vendor/NOTICE.md](vendor/NOTICE.md) per provenienza, licenza e sorgenti della distribuzione locale. Soundboard e un progetto indipendente e non e affiliato a Soundpad, Leppsoft, VB-Audio o Discord.

## Pubblicazione su GitHub

Da un normale PowerShell di Windows, con accesso GitHub tramite il gestore di credenziali Git:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-github.ps1
```

Lo script crea il commit locale, crea una repository privata `Soundboard` nell'account autenticato e carica il branch `main`. Per scegliere il nome usare `-RepositoryName Nome`; per la visibilita pubblica aggiungere `-Public`. Non esegue push forzati e non salva credenziali nei file del progetto o nell'URL del remoto.
