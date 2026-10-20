using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class AudioRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        var directory = args[0];
        var decoder = args[1];
        var actualFile = args.Length > 2 ? args[2] : null;
        var physical = NativeAudio.PlaybackDevices().First(device => device.Name.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0);
        var virtualDevice = AudioRouting.VirtualDevices().First();
        using (var player = new PcmAudioPlayer { CacheDirectory = Path.Combine(directory, "cache"), DecoderPath = decoder })
        {
            var mp4 = Path.Combine(directory, "tone.mp4");
            Wait(player.OpenAsync(mp4, new[] { physical.Number }));
            Check(player.Duration.TotalSeconds > 5.9 && player.Duration.TotalSeconds < 6.2, "Durata MP4 con video");
            player.Volume = 0.8;
            player.Play();
            Thread.Sleep(220);
            var full = QueuedRms(player);
            player.Volume = 0.2;
            Thread.Sleep(220);
            var quiet = QueuedRms(player);
            Check(full > 100 && Math.Abs(quiet / full - 0.25) < 0.04, "Volume MP4 modifica realmente i campioni inviati");
            player.Volume = 0;
            Thread.Sleep(220);
            Check(QueuedRms(player) == 0, "Volume zero produce silenzio digitale");
            player.Pause();
            var before = player.Position;
            Thread.Sleep(80);
            Check(Math.Abs((player.Position - before).TotalMilliseconds) < 30, "La pausa arresta l'avanzamento");
            player.Position = TimeSpan.FromSeconds(2);
            Check(Math.Abs(player.Position.TotalSeconds - 2) < 0.03, "Seek in pausa su MP4");
            player.Volume = 0.13;
            player.Close();
            Check(Math.Abs(player.Volume - 0.13) < 0.0001, "Stop conserva il volume");
            Wait(player.OpenAsync(mp4, new[] { physical.Number }));
            Check(Math.Abs(player.Volume - 0.13) < 0.0001, "Apertura successiva conserva il volume");
            player.Volume = 0;
            player.Play();
            Thread.Sleep(120);
            Check(QueuedRms(player) == 0, "La riapertura applica il volume prima della riproduzione");
            player.Close();

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var opening = player.OpenAsync(mp4, new[] { physical.Number });
                player.Close();
                var canceled = false;
                try { Wait(opening); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && player.Source == null && player.ActiveDevices.Length == 0, "Stop annulla il caricamento senza avvio tardivo");
            }
            var earlier = player.OpenAsync(mp4, new[] { physical.Number });
            var newer = player.OpenAsync(Path.Combine(directory, "silence.wav"), new[] { physical.Number });
            Wait(newer);
            try { Wait(earlier); }
            catch (OperationCanceledException) { }
            Check(player.Source.LocalPath.EndsWith("silence.wav"), "Cambio rapido conserva soltanto l'ultimo file");
            player.Close();

            var noAudio = false;
            try { Wait(player.OpenAsync(Path.Combine(directory, "video-only.mp4"), new[] { physical.Number })); }
            catch (InvalidDataException) { noAudio = true; }
            Check(noAudio && player.Source == null && player.ActiveDevices.Length == 0, "Video senza audio rifiutato senza uscite residue");

            if (actualFile != null && File.Exists(actualFile))
            {
                player.Volume = 0;
                Wait(player.OpenAsync(actualFile, new[] { physical.Number }));
                player.Play();
                Thread.Sleep(120);
                Check(player.Duration.TotalSeconds > 0 && QueuedRms(player) == 0, "File M4A dell'utente decodificato e riprodotto a volume zero");
                player.Close();
            }

            player.Volume = 0;
            var microphone = AudioRouting.Resolve(AudioRoute.Microphone, virtualDevice.Name);
            Wait(player.OpenAsync(Path.Combine(directory, "silence.wav"), microphone));
            player.Play();
            Thread.Sleep(100);
            Check(player.ActiveDevices.SequenceEqual(new[] { virtualDevice.Number }), "Solo microfono apre esclusivamente il dispositivo virtuale");
            Check(QueuedRms(player) == 0, "Test sul microfono virtuale limitato al silenzio");
            player.Close();
            var speakers = AudioRouting.Resolve(AudioRoute.Speakers, null);
            Wait(player.OpenAsync(Path.Combine(directory, "silence.wav"), speakers));
            player.Play();
            Check(player.ActiveDevices.SequenceEqual(speakers), "Uscita predefinita risolta e aperta esplicitamente");
            player.Close();
            var both = AudioRouting.Resolve(AudioRoute.Both, virtualDevice.Name);
            Wait(player.OpenAsync(Path.Combine(directory, "silence.wav"), both));
            player.Play();
            Check(player.ActiveDevices.SequenceEqual(both), "Modalita entrambe apre le uscite richieste senza duplicati");
            player.Close();

            var unavailable = false;
            try { AudioRouting.Resolve(AudioRoute.Microphone, "Dispositivo inesistente per test"); }
            catch (InvalidOperationException) { unavailable = true; }
            Check(unavailable, "Nessun ripiego sugli altoparlanti con microfono mancante");
            using (var completed = new ManualResetEvent(false))
            {
                player.PlaybackStopped += delegate(object sender, AudioPlaybackEventArgs e) { if (e.Error == null) completed.Set(); };
                Wait(player.OpenAsync(Path.Combine(directory, "silence.wav"), new[] { physical.Number }));
                player.Position = player.Duration - TimeSpan.FromMilliseconds(60);
                player.Play();
                Check(completed.WaitOne(2000), "Fine naturale della riproduzione notificata");
                player.Close();
            }
            Check(Directory.GetFiles(player.CacheDirectory, "*.pcm").Length == 0, "Cache ripulita dopo errori, annullamenti e stop");
        }
        VerifyWindow(directory, decoder, virtualDevice.Name);
    }

    private static void VerifyWindow(string directory, string decoder, string virtualName)
    {
        var form = new SoundboardWindow();
        SoundboardWindow restored = null;
        try
        {
            typeof(SoundboardWindow).GetField("libraryPath", Fields).SetValue(form, Path.Combine(directory, "library.xml"));
            var player = (PcmAudioPlayer)typeof(SoundboardWindow).GetField("player", Fields).GetValue(form);
            player.CacheDirectory = Path.Combine(directory, "window-cache");
            player.DecoderPath = decoder;
            var sounds = (System.ComponentModel.BindingList<SoundEntry>)typeof(SoundboardWindow).GetField("sounds", Fields).GetValue(form);
            sounds.Clear();
            sounds.Add(new SoundEntry { Path = Path.Combine(directory, "silence.wav") });
            form.Show();
            Application.DoEvents();
            typeof(SoundboardWindow).GetField("usePhysicalMicrophone", Fields).SetValue(form, false);
            var microphone = (Button)typeof(SoundboardWindow).GetField("microphoneButton", Fields).GetValue(form);
            Check(microphone.Enabled, "Pulsante microfono selezionabile");
            typeof(SoundboardWindow).GetField("microphoneDeviceName", Fields).SetValue(form, "Dispositivo inesistente per test");
            microphone.PerformClick();
            Application.DoEvents();
            var status = (Label)typeof(SoundboardWindow).GetField("status", Fields).GetValue(form);
            Check(player.ActiveDevices.Length == 0 && status.Text.StartsWith("Riproduzione non riuscita"), "Interfaccia segnala il microfono mancante senza riprodurre altrove");
            typeof(SoundboardWindow).GetField("microphoneDeviceName", Fields).SetValue(form, virtualName);
            var volume = (AudioSlider)typeof(SoundboardWindow).GetField("volume", Fields).GetValue(form);
            volume.Value = 174;
            form.Close();
            restored = new SoundboardWindow();
            typeof(SoundboardWindow).GetField("libraryPath", Fields).SetValue(restored, Path.Combine(directory, "library.xml"));
            typeof(SoundboardWindow).GetMethod("LoadAudioSettings", Fields).Invoke(restored, null);
            var restoredVolume = (AudioSlider)typeof(SoundboardWindow).GetField("volume", Fields).GetValue(restored);
            var restoredRoute = (AudioRoute)typeof(SoundboardWindow).GetField("outputRoute", Fields).GetValue(restored);
            var restoredDevice = (string)typeof(SoundboardWindow).GetField("microphoneDeviceName", Fields).GetValue(restored);
            Check(restoredVolume.Value == 174 && restoredRoute == AudioRoute.Microphone && restoredDevice == virtualName, "Volume, modalita e dispositivo persistono al riavvio");
        }
        finally
        {
            if (!form.IsDisposed) form.Close();
            form.Dispose();
            if (restored != null) { restored.Show(); restored.Close(); restored.Dispose(); }
        }
    }

    private static double QueuedRms(PcmAudioPlayer player)
    {
        var outputs = (List<PcmOutput>)typeof(PcmAudioPlayer).GetField("outputs", Fields).GetValue(player);
        var output = outputs[0];
        var gate = typeof(PcmOutput).GetField("gate", Fields).GetValue(output);
        lock (gate)
        {
            var buffers = (List<IntPtr>)typeof(PcmOutput).GetField("buffers", Fields).GetValue(output);
            var headers = (List<IntPtr>)typeof(PcmOutput).GetField("headers", Fields).GetValue(output);
            var squares = 0.0;
            var count = 0;
            foreach (var index in Enumerable.Range(0, headers.Count))
            {
                var header = (NativeAudio.Header)Marshal.PtrToStructure(headers[index], typeof(NativeAudio.Header));
                if ((header.Flags & 16) == 0) continue;
                var samples = new byte[header.Length];
                Marshal.Copy(buffers[index], samples, 0, samples.Length);
                squares += Enumerable.Range(0, samples.Length / 2).Sum(sample => Math.Pow(BitConverter.ToInt16(samples, sample * 2), 2));
                count += samples.Length / 2;
            }
            if (count == 0) throw new InvalidOperationException("Nessun buffer audio accodato per la misura.");
            return Math.Sqrt(squares / count);
        }
    }

    private static void Wait(Task task)
    {
        if (!SpinWait.SpinUntil(() => task.IsCompleted, 10000)) throw new TimeoutException("Timeout caricamento audio.");
        task.GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }
}
