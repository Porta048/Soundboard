# Decoder FFmpeg locale

La distribuzione locale usata nello sviluppo e FFmpeg 8.1.1 full, build Gyan, pacchetto WinGet Gyan.FFmpeg. `ffmpeg.exe` non e incluso nel repository Git: procurarsi il decoder separatamente e copiarlo in questa cartella.

Copyright FFmpeg developers. Build Gyan, con configurazione GPL v3. La licenza della distribuzione e in `FFmpeg-LICENSE.txt`; il README originale della build e in `FFmpeg-README.txt`.

- Progetto e sorgenti: https://ffmpeg.org/
- Distribuzione della build: https://www.gyan.dev/ffmpeg/builds/

Soundboard esegue questo programma come processo separato per estrarre la prima traccia audio locale in PCM stereo 48 kHz a 16 bit. Il processo non apre finestre e viene annullato quando il caricamento e interrotto. I protocolli d'ingresso sono limitati a file e pipe.

Conservare `vendor` accanto a `Soundboard.exe` quando si sposta l'applicazione. La build completa del decoder occupa circa 217 MiB.
