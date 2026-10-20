using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal sealed class AudioPlaybackEventArgs : EventArgs
{
    public Exception Error { get; set; }
    public long Session { get; set; }
}

internal sealed class PcmOutput : IDisposable
{
    private readonly object gate = new object();
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly List<IntPtr> headers = new List<IntPtr>();
    private readonly List<IntPtr> buffers = new List<IntPtr>();
    private readonly byte[] samples = new byte[7680];
    private readonly FileStream source;
    private readonly uint headerSize = (uint)Marshal.SizeOf(typeof(NativeAudio.Header));
    private readonly int flagsOffset = (int)Marshal.OffsetOf(typeof(NativeAudio.Header), "Flags");
    private IntPtr handle;
    private Thread worker;
    private bool disposed;
    private bool paused = true;
    private bool finished;
    private long seekOffset;
    private long positionWrap;
    private uint lastPosition;
    private volatile float gain = 0.8f;
    public event EventHandler<AudioPlaybackEventArgs> Ended;
    public int DeviceNumber { get; private set; }
    public bool Finished { get { lock (gate) return finished; } }
    public float Volume { get { return gain; } set { gain = Math.Max(0, Math.Min(1, value)); } }

    public PcmOutput(string path, int device)
    {
        DeviceNumber = device;
        source = File.OpenRead(path);
        try
        {
            var format = new NativeAudio.Format { Tag = 1, Channels = 2, SampleRate = 48000, BytesPerSecond = 192000, BlockAlign = 4, Bits = 16 };
            NativeAudio.Check(NativeAudio.waveOutOpen(out handle, device, ref format, IntPtr.Zero, IntPtr.Zero, 0), "Apertura uscita audio");
            NativeAudio.Check(NativeAudio.waveOutPause(handle), "Pausa uscita audio");
            foreach (var index in Enumerable.Range(0, 3))
            {
                var data = Marshal.AllocHGlobal(samples.Length);
                var header = Marshal.AllocHGlobal((int)headerSize);
                Marshal.StructureToPtr(new NativeAudio.Header { Data = data, Length = (uint)samples.Length }, header, false);
                var result = NativeAudio.waveOutPrepareHeader(handle, header, headerSize);
                if (result != 0)
                {
                    Marshal.FreeHGlobal(header);
                    Marshal.FreeHGlobal(data);
                    NativeAudio.Check(result, "Preparazione buffer audio");
                }
                buffers.Add(data);
                headers.Add(header);
            }
            worker = new Thread(Pump) { IsBackground = true, Name = "Soundboard audio " + device };
            worker.Start();
        }
        catch { Dispose(); throw; }
    }

    public TimeSpan Position
    {
        get
        {
            lock (gate)
            {
                if (disposed) return TimeSpan.Zero;
                var time = new NativeAudio.Time { Type = 4 };
                NativeAudio.Check(NativeAudio.waveOutGetPosition(handle, ref time, (uint)Marshal.SizeOf(typeof(NativeAudio.Time))), "Posizione audio");
                var bytes = time.Type == 2 ? time.Value * 4L : time.Type == 1 ? time.Value * 192L : time.Value;
                if (time.Type == 4)
                {
                    if (time.Value < lastPosition) positionWrap += 0x100000000L;
                    lastPosition = time.Value;
                    bytes += positionWrap;
                }
                return TimeSpan.FromSeconds(Math.Min(source.Length, seekOffset + bytes) / 192000.0);
            }
        }
    }

    public void Play()
    {
        lock (gate)
        {
            if (disposed || finished) return;
            NativeAudio.Check(NativeAudio.waveOutRestart(handle), "Ripresa audio");
            paused = false;
        }
        wake.Set();
    }

    public void Pause()
    {
        lock (gate)
        {
            if (disposed || finished) return;
            NativeAudio.Check(NativeAudio.waveOutPause(handle), "Pausa audio");
            paused = true;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (gate)
        {
            if (disposed || finished) return;
            NativeAudio.Check(NativeAudio.waveOutReset(handle), "Spostamento audio");
            var offset = Math.Max(0, Math.Min(source.Length, (long)(position.TotalSeconds * 192000)));
            source.Position = offset - offset % 4;
            seekOffset = source.Position;
            positionWrap = 0;
            lastPosition = 0;
            NativeAudio.Check(paused ? NativeAudio.waveOutPause(handle) : NativeAudio.waveOutRestart(handle), "Ripristino uscita audio");
        }
        wake.Set();
    }

    private void Pump()
    {
        Exception failure = null;
        try
        {
            while (true)
            {
                lock (gate)
                {
                    if (disposed) return;
                    if (!paused)
                    {
                        foreach (var index in Enumerable.Range(0, headers.Count))
                        {
                            if ((Marshal.ReadInt32(headers[index], flagsOffset) & 16) != 0) continue;
                            var count = source.Read(samples, 0, samples.Length);
                            if (count == 0) continue;
                            var volume = gain;
                            for (var offset = 0; offset < count; offset += 2)
                            {
                                var sample = (short)(samples[offset] | samples[offset + 1] << 8);
                                var scaled = (short)(sample * volume);
                                samples[offset] = (byte)scaled;
                                samples[offset + 1] = (byte)(scaled >> 8);
                            }
                            Marshal.Copy(samples, 0, buffers[index], count);
                            Marshal.WriteInt32(headers[index], IntPtr.Size, count);
                            NativeAudio.Check(NativeAudio.waveOutWrite(handle, headers[index], headerSize), "Invio audio");
                        }
                        if (source.Position == source.Length && headers.All(header => (Marshal.ReadInt32(header, flagsOffset) & 16) == 0))
                        {
                            finished = true;
                            break;
                        }
                    }
                }
                wake.WaitOne(10);
            }
        }
        catch (Exception error) { failure = error; lock (gate) finished = true; }
        var ended = Ended;
        if (ended != null) ended(this, new AudioPlaybackEventArgs { Error = failure });
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (handle != IntPtr.Zero) NativeAudio.waveOutReset(handle);
        }
        wake.Set();
        if (worker != null && worker != Thread.CurrentThread) worker.Join();
        foreach (var header in headers)
        {
            if (handle != IntPtr.Zero) NativeAudio.waveOutUnprepareHeader(handle, header, headerSize);
            Marshal.FreeHGlobal(header);
        }
        if (handle != IntPtr.Zero) NativeAudio.waveOutClose(handle);
        foreach (var buffer in buffers) Marshal.FreeHGlobal(buffer);
        source.Dispose();
        wake.Dispose();
        handle = IntPtr.Zero;
    }
}

internal sealed class PcmAudioPlayer : IDisposable
{
    private readonly object gate = new object();
    private readonly List<PcmOutput> outputs = new List<PcmOutput>();
    private ApoPcmOutput microphoneOutput;
    private CancellationTokenSource opening;
    private string decodedPath;
    private float gain = 0.8f;
    private long session;
    private long outputSession;
    private bool ended;
    public event EventHandler<AudioPlaybackEventArgs> PlaybackStopped;
    public string CacheDirectory { get; set; }
    public string DecoderPath { get; set; }
    public Uri Source { get; private set; }
    public TimeSpan Duration { get; private set; }
    public long Session { get { return Interlocked.Read(ref session); } }
    public int[] ActiveDevices { get { lock (gate) return outputs.Select(output => output.DeviceNumber).ToArray(); } }

    public PcmAudioPlayer()
    {
        CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundboard", "cache");
        DecoderPath = Path.Combine(Path.GetDirectoryName(typeof(PcmAudioPlayer).Assembly.Location), "vendor", "ffmpeg.exe");
    }

    public double Volume
    {
        get { return gain; }
        set
        {
            lock (gate)
            {
                gain = (float)Math.Max(0, Math.Min(1, value));
                foreach (var output in outputs) output.Volume = gain;
                if (microphoneOutput != null) microphoneOutput.Volume = gain;
            }
        }
    }

    public TimeSpan Position
    {
        get { lock (gate) return microphoneOutput != null ? microphoneOutput.Position : outputs.Count == 0 ? TimeSpan.Zero : outputs[0].Position; }
        set { lock (gate) { foreach (var output in outputs) output.Seek(value); if (microphoneOutput != null) microphoneOutput.Seek(value); } }
    }

    public async Task OpenAsync(string path, int[] devices, string microphoneEndpoint = null)
    {
        Close();
        if (!File.Exists(path)) throw new FileNotFoundException("File audio non trovato.", path);
        if (!File.Exists(DecoderPath)) throw new FileNotFoundException("Decoder audio mancante: conserva la cartella vendor accanto a Soundboard.exe.", DecoderPath);
        if (devices.Length == 0 && microphoneEndpoint == null) throw new InvalidOperationException("Nessuna uscita audio selezionata.");
        var request = Session;
        var cancellation = new CancellationTokenSource();
        opening = cancellation;
        string decoded = null;
        try
        {
            decoded = await Task.Run(() => Decode(path, cancellation.Token)).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != Session) throw new OperationCanceledException();
            lock (gate)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (request != Session) throw new OperationCanceledException();
                decodedPath = decoded;
                decoded = null;
                Source = new Uri(Path.GetFullPath(path));
                Duration = TimeSpan.FromSeconds(new FileInfo(decodedPath).Length / 192000.0);
                outputSession = request;
                ended = false;
                if (microphoneEndpoint != null)
                {
                    microphoneOutput = new ApoPcmOutput(decodedPath, ApoPcmOutput.ChannelName(microphoneEndpoint), CacheDirectory) { Volume = gain };
                    microphoneOutput.Ended += OutputEnded;
                }
                foreach (var device in devices.Distinct())
                {
                    var output = new PcmOutput(decodedPath, device) { Volume = gain };
                    output.Ended += OutputEnded;
                    outputs.Add(output);
                }
            }
        }
        catch
        {
            if (request == Session) Close();
            throw;
        }
        finally
        {
            lock (gate)
            {
                if (opening == cancellation) opening = null;
                cancellation.Dispose();
            }
            if (decoded != null) DeleteDecodedFile(decoded);
        }
    }

    private string Decode(string path, CancellationToken cancellation)
    {
        Directory.CreateDirectory(CacheDirectory);
        var outputPath = Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N") + ".pcm");
        try
        {
            var start = new ProcessStartInfo {
                FileName = DecoderPath,
                Arguments = "-nostdin -hide_banner -loglevel error -y -protocol_whitelist file,pipe -i \"" + Path.GetFullPath(path) + "\" -map 0:a:0 -vn -sn -dn -ac 2 -ar 48000 -c:a pcm_s16le -f s16le \"" + outputPath + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = start })
            {
                cancellation.ThrowIfCancellationRequested();
                process.Start();
                using (cancellation.Register(CancelDecoder, process))
                {
                    var errors = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    var errorText = errors.GetAwaiter().GetResult().Trim();
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new InvalidDataException(string.IsNullOrEmpty(errorText) ? "Il file non contiene una traccia audio leggibile." : errorText);
                }
            }
            var length = new FileInfo(outputPath).Length;
            if (length == 0 || length % 4 != 0) throw new InvalidDataException("Il file non contiene campioni audio validi.");
            return outputPath;
        }
        catch { DeleteDecodedFile(outputPath); throw; }
    }

    private static void DeleteDecodedFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException error) { Trace.TraceWarning("Soundboard cache: " + error.Message); }
        catch (UnauthorizedAccessException error) { Trace.TraceWarning("Soundboard cache: " + error.Message); }
    }

    private static void CancelDecoder(object state)
    {
        try { var process = (Process)state; if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void OutputEnded(object sender, AudioPlaybackEventArgs e)
    {
        long current;
        lock (gate)
        {
            if (ended || (sender != microphoneOutput && !outputs.Cast<object>().Contains(sender))) return;
            if (e.Error == null && (outputs.Any(output => !output.Finished) || (microphoneOutput != null && !microphoneOutput.Finished))) return;
            ended = true;
            current = outputSession;
        }
        var stopped = PlaybackStopped;
        if (stopped != null) stopped(this, new AudioPlaybackEventArgs { Error = e.Error, Session = current });
    }

    public void Play() { lock (gate) { if (microphoneOutput != null) microphoneOutput.Play(); foreach (var output in outputs) output.Play(); } }
    public void Pause() { lock (gate) { if (microphoneOutput != null) microphoneOutput.Pause(); foreach (var output in outputs) output.Pause(); } }

    public void Close()
    {
        PcmOutput[] previous;
        ApoPcmOutput previousMicrophone;
        string path;
        lock (gate)
        {
            Interlocked.Increment(ref session);
            if (opening != null) opening.Cancel();
            previous = outputs.ToArray();
            outputs.Clear();
            previousMicrophone = microphoneOutput;
            microphoneOutput = null;
            if (previousMicrophone != null) previousMicrophone.Ended -= OutputEnded;
            path = decodedPath;
            decodedPath = null;
            Source = null;
            Duration = TimeSpan.Zero;
            foreach (var output in previous) output.Ended -= OutputEnded;
        }
        foreach (var output in previous) output.Dispose();
        if (previousMicrophone != null) previousMicrophone.Dispose();
        if (path != null) DeleteDecodedFile(path);
    }

    public void Dispose() { Close(); }
}
