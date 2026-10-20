using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

internal static class MicrophoneBridgeRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private static double Meter(Process host)
    {
        host.StandardInput.WriteLine("meter");
        host.StandardInput.Flush();
        return double.Parse(host.StandardOutput.ReadLine(), CultureInfo.InvariantCulture);
    }

    private static int Main(string[] args)
    {
        Process host = null;
        try
        {
            var directory = args[0];
            var hostPath = args[1];
            var dll = args[2];
            var pcm = Path.Combine(directory, "constant.pcm");
            using (var writer = new BinaryWriter(File.Create(pcm)))
                for (var frame = 0; frame < 48000 * 8; frame++) { writer.Write((short)8192); writer.Write((short)4096); }
            var channel = "Local\\Soundboard.Audio.v1.managed-" + Guid.NewGuid().ToString("N");
            host = Process.Start(new ProcessStartInfo {
                FileName = hostPath, Arguments = "\"" + dll + "\" --serve \"" + channel + "\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            });
            var ready = host.StandardOutput.ReadLineAsync();
            Check(ready.Wait(5000) && ready.Result == "READY", "Host APO isolato avviato senza dispositivi audio reali");
            using (var output = new ApoPcmOutput(pcm, channel, directory))
            {
                var rejected = false;
                try { using (var other = new ApoPcmOutput(pcm, channel, directory)) { } }
                catch (IOException) { rejected = true; }
                Check(rejected, "Un solo produttore per microfono");
                output.Volume = 0.8f;
                output.Play();
                Thread.Sleep(180);
                Check(Math.Abs(Meter(host) - 0.15) < 0.002, "Campioni del player ricevuti e miscelati dalla DLL");
                output.Volume = 0.25f;
                Thread.Sleep(50);
                Check(Math.Abs(Meter(host) - 0.046875) < 0.002, "Volume applicato nel flusso APO");
                output.Volume = 0;
                Thread.Sleep(50);
                Check(Meter(host) == 0, "Volume zero nel flusso APO");
                output.Pause();
                var position = output.Position;
                Thread.Sleep(60);
                Check(Math.Abs((output.Position - position).TotalMilliseconds) < 1, "Pausa mantiene la posizione");
                output.Seek(TimeSpan.FromSeconds(3));
                Check(Math.Abs(output.Position.TotalSeconds - 3) < 0.001, "Ricerca nel file in pausa");
                output.Volume = 0.8f;
                output.Play();
                Thread.Sleep(120);
                Check(Math.Abs(Meter(host) - 0.15) < 0.002, "Ripresa dopo ricerca senza campioni residui");
                using (var completed = new ManualResetEvent(false))
                {
                    output.Ended += (sender, ended) => { if (ended.Error == null) completed.Set(); };
                    output.Seek(TimeSpan.FromSeconds(7.9));
                    Check(completed.WaitOne(2000), "Fine naturale del file notificata");
                }
            }
            Thread.Sleep(50);
            Check(Meter(host) == 0, "Stop rimuove il file dal microfono");
            Check(Directory.GetFiles(directory, "*.lock").Length == 0, "Blocco produttore rilasciato");
            var absent = false;
            try { using (var output = new ApoPcmOutput(pcm, channel + "-missing", directory)) { } }
            catch (InvalidOperationException error) { absent = error.Message.Contains("assente"); }
            Check(absent, "Componente assente segnalato senza ripiego sugli altoparlanti");
            var wav = Path.Combine(directory, "constant.wav");
            using (var writer = new BinaryWriter(File.Create(wav)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write((int)new FileInfo(pcm).Length + 36);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)2);
                writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write((int)new FileInfo(pcm).Length); writer.Write(File.ReadAllBytes(pcm));
            }
            using (var player = new PcmAudioPlayer { CacheDirectory = Path.Combine(directory, "cache"), DecoderPath = args[3] })
            {
                var missing = false;
                try { player.OpenAsync(wav, new[] { NativeAudio.PreferredPlaybackDevice() }, "{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}").GetAwaiter().GetResult(); }
                catch (InvalidOperationException error) { missing = error.Message.Contains("assente"); }
                Check(missing && player.Source == null && player.ActiveDevices.Length == 0, "Player completo non apre altoparlanti se manca il collegamento fisico");
                Check(Directory.GetFiles(player.CacheDirectory, "*.pcm").Length == 0, "Decodifica annullata ripulisce la cache se manca APO");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (host != null)
            {
                if (!host.HasExited) { host.StandardInput.WriteLine("quit"); host.StandardInput.Close(); host.WaitForExit(5000); }
                host.Dispose();
            }
        }
    }
}
