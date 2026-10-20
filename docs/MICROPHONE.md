# Collegamento al microfono fisico

## Stato

Implementazione sperimentale del percorso FFmpeg -> PCM -> memoria condivisa -> APO -> flusso di acquisizione del microfono. Il codice usa le interfacce COM pubbliche di Windows; non usa DLL o protocolli Soundpad e non richiede Voicemeeter.

La DLL e il collegamento dal player sono stati verificati con un host COM isolato. Non considerare i test isolati come prova della ricezione da altri utenti Discord.

**Esito sul sistema reale (Windows 11, build 26200, microfono USB con Voice Clarity):** non funzionante. Con l'APO installato il microfono non si apre piu in modalita condivisa (`IAudioClient::Initialize` restituisce `AUDCLNT_E_DEVICE_INVALIDATED`), quindi nemmeno Discord riesce a usarlo; la modalita esclusiva funziona. Le prove hanno escluso slot (EFX e SFX composito), flag di registrazione, `Disable_SysFx` e Voice Clarity: lo stesso slot SFX con l'APO firmato di un altro produttore funziona, mentre la nostra DLL non firmata viene scartata dal motore audio prima della creazione, senza eventi in Code Integrity. Per usarlo serve una DLL con firma digitale valida. Il percorso consigliato e Voicemeeter, descritto nel [README](../README.md).

Correzioni gia applicate durante le prove: supporto dell'aggregazione COM (il motore audio crea gli APO aggregati), registrazione allineata agli SFX funzionanti (`APO_FLAG_DEFAULT`, una sola interfaccia), backup esteso alle nuove proprieta e traccia ETW `Soundboard.Apo` (`{6b1f3c52-8e4a-4d27-9c3b-51a07e2d94f6}`) per diagnosticare il passaggio che fallisce.

## Installazione sul PC di sviluppo

1. Compilare con `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1`.
2. Avviare `bin\Soundboard.exe`.
3. In **Riproduci > Microfono fisico**, lasciare **Automatico (predefinito comunicazioni)** oppure scegliere il dispositivo usato in Discord.
4. Aprire **Riproduci > Configura microfono fisico**. Il riepilogo mostra il dispositivo selezionato; l'installazione richiede amministratore tramite la richiesta UAC di Windows.
5. Riaprire Discord, selezionare lo stesso microfono fisico e avviare il test microfono. Se Windows non carica la nuova configurazione, riavviare il PC.
6. Riprodurre un file con **Riproduci file solo nel microfono**. Verificare nel test Discord sia la propria voce sia il file. Le chiamate con altre persone vanno provate successivamente.

L'automatismo segue il microfono predefinito di Windows per le comunicazioni. Non legge la selezione privata interna di Discord: se Discord usa un dispositivo diverso, selezionare quello anche in Soundboard.

L'installatore copia la DLL in una cartella versionata in `%ProgramFiles%\SoundboardAudio`, registra la classe COM, salva le proprieta originali del dispositivo e aggiunge l'APO alla lista composita degli effetti di stream (SFX, `PKEY_CompositeFX_StreamEffectClsid`) per le modalita Default e Communications. Gli effetti di altri produttori nella lista restano invariati; gli slot MFX ed EFX non vengono toccati. Lo slot EFX usato dalle versioni precedenti viene ripristinato: Windows lo scarta quando sul microfono e attivo Voice Clarity. Non chiude applicazioni e non riavvia servizi audio automaticamente.

La build e non firmata. Questo installatore accetta soltanto PC dove `DisableProtectedAudioDG=1` e gia presente; non crea o modifica tale impostazione. Sul PC di sviluppo controllato era gia impostata. Per distribuire ad altri PC serve completare il pacchetto e la firma appropriata; non e una soluzione pronta per la distribuzione generale. Riferimento: [implementazione e distribuzione degli APO Microsoft](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects).

Controllo preliminare senza modifiche di sistema, usando l'identificativo del dispositivo restituito da Windows:

```powershell
.\scripts\install-microphone.ps1 -EndpointId '<ID del microfono>' -CheckOnly
```

## Ripristino

Da Windows PowerShell a 64 bit avviato come amministratore:

```powershell
.\scripts\install-microphone.ps1 -EndpointId '<ID del microfono>' -Uninstall
```

Il comando ripristina i valori salvati nel backup specifico del dispositivo, sotto `%ProgramFiles%\SoundboardAudio`. Non cancella il backup prima che il ripristino sia riuscito. Le DLL restano archiviate per non eliminare moduli eventualmente ancora caricati. Riaprire le applicazioni audio; se necessario riavviare Windows.

## Trasferimento e prove

`native/AudioBridge.h` definisce un'intestazione da 128 byte e un buffer circolare stereo PCM 16 bit, 48 kHz, da 8192 frame. L'APO mantiene la voce, adatta il file alla frequenza e al numero di canali del dispositivo, applica il guadagno e limita la somma a [-1,1]. Il processo audio crea il canale condiviso con accesso per il proprietario configurato e i servizi di sistema. Un blocco su file impedisce due produttori contemporanei. Un heartbeat interrompe l'aggiunta del file se il produttore scompare.

I test coprono voce invariata a riproduzione ferma, voce piu file, ingresso silenzioso, stereo e mono, 48/44.1 kHz, volume zero, picchi, pausa, produttore disconnesso, lettori contemporanei, memoria condivisa, ricerca, fine naturale, stop e rilascio delle risorse. Il player verifica il canale prima di aprire eventuali altoparlanti: se manca il collegamento, restituisce un errore esplicito.

Restano da validare sul dispositivo reale: caricamento EFX, convivenza con altri componenti audio, latenza e stabilita prolungata, cambi del dispositivo durante una chiamata e l'effetto dei filtri vocali di Discord. Un test fallito non va risolto disattivando effetti o protezioni alla cieca.
