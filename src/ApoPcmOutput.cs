using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Principal;
using System.Threading;

internal sealed class ApoPcmOutput : IDisposable
{
    private const long Capacity = 8192;
    private const long MappingBytes = 128 + Capacity * 4;
    private readonly object gate = new object();
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly byte[] samples = new byte[4096];
    private FileStream source;
    private FileStream writerLock;
    private MemoryMappedFile mapping;
    private MemoryMappedViewAccessor view;
    private Thread worker;
    private bool disposed;
    private bool paused = true;
    private bool finished;
    private long baseFrame;
    private long startQpc;
    public event EventHandler<AudioPlaybackEventArgs> Ended;
    public bool Finished { get { lock (gate) return finished; } }

    public static string ChannelName(string endpointId)
    {
        var start = endpointId == null ? -1 : endpointId.LastIndexOf('{');
        Guid id;
        if (start < 0 || !Guid.TryParse(endpointId.Substring(start), out id)) throw new InvalidOperationException("Identificativo microfono non valido.");
        using (var identity = WindowsIdentity.GetCurrent())
            return "Global\\Soundboard.Audio.v1." + id.ToString("B") + "." + identity.User.Value;
    }

    public ApoPcmOutput(string path, string channel, string cacheDirectory)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var lockName = channel.Substring(channel.LastIndexOf('\\') + 1) + ".lock";
            writerLock = new FileStream(Path.Combine(cacheDirectory, lockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            source = File.OpenRead(path);
            try { mapping = MemoryMappedFile.OpenExisting(channel, MemoryMappedFileRights.ReadWrite); }
            catch (FileNotFoundException) { throw new InvalidOperationException("Collegamento Soundboard assente sul microfono. Usa Riproduci > Configura microfono fisico, poi riapri Discord e avvia il test microfono o una chiamata."); }
            view = mapping.CreateViewAccessor(0, MappingBytes, MemoryMappedFileAccess.ReadWrite);
            if (view.ReadUInt32(0) != 0x31414253 || view.ReadUInt32(4) != 1 || view.ReadInt64(80) != Capacity || view.ReadInt64(72) != Stopwatch.Frequency)
                throw new InvalidDataException("Versione del componente microfono non compatibile.");
            var wait = Stopwatch.StartNew();
            while (!CaptureActive() && wait.ElapsedMilliseconds < 300) Thread.Sleep(10);
            if (!CaptureActive()) throw new InvalidOperationException("Il microfono non e in acquisizione. In Discord seleziona questo stesso microfono e avvia il test microfono o una chiamata.");
            Prepare(0);
            worker = new Thread(Pump) { IsBackground = true, Name = "Soundboard microphone" };
            worker.Start();
        }
        catch { Dispose(); throw; }
    }

    public float Volume
    {
        set { lock (gate) if (!disposed) view.Write(12, (int)(Math.Max(0, Math.Min(1, value)) * 1000)); }
    }

    public TimeSpan Position
    {
        get { lock (gate) return disposed ? TimeSpan.Zero : TimeSpan.FromSeconds(CurrentFrame() / 48000.0); }
    }

    private long CurrentFrame()
    {
        var frame = baseFrame + (paused ? 0 : (long)((Stopwatch.GetTimestamp() - startQpc) * (48000.0 / Stopwatch.Frequency)));
        return Math.Max(0, Math.Min(source.Length / 4, frame));
    }

    private bool CaptureActive()
    {
        var last = view.ReadInt64(64);
        var now = Stopwatch.GetTimestamp();
        return last > 0 && now >= last && now - last < Stopwatch.Frequency;
    }

    private void Prepare(long frame)
    {
        view.Write(8, 0);
        Thread.MemoryBarrier();
        baseFrame = frame;
        source.Position = frame * 4;
        view.Write(32, frame);
        view.Write(40, frame);
        view.Write(48, source.Length / 4);
        startQpc = Stopwatch.GetTimestamp();
        view.Write(24, startQpc);
        FillAhead(frame);
        view.Write(16, view.ReadInt64(16) + 1);
        view.Write(56, Stopwatch.GetTimestamp());
        Thread.MemoryBarrier();
    }

    private void FillAhead(long frame)
    {
        var target = Math.Min(source.Length / 4, frame + Capacity / 2);
        while (source.Position / 4 < target)
        {
            var writeFrame = source.Position / 4;
            var ringOffset = writeFrame % Capacity;
            var frames = Math.Min(target - writeFrame, Math.Min(Capacity - ringOffset, samples.Length / 4));
            var count = source.Read(samples, 0, (int)frames * 4);
            if (count == 0) throw new EndOfStreamException("Campioni audio incompleti.");
            view.WriteArray(128 + ringOffset * 4, samples, 0, count);
            Thread.MemoryBarrier();
            view.Write(40, source.Position / 4);
        }
    }

    public void Play()
    {
        lock (gate)
        {
            if (disposed || finished || !paused) return;
            if (!CaptureActive()) throw new InvalidOperationException("Acquisizione del microfono interrotta. Riapri il test microfono o la chiamata Discord.");
            Prepare(baseFrame);
            paused = false;
            view.Write(56, Stopwatch.GetTimestamp());
            Thread.MemoryBarrier();
            view.Write(8, 1);
        }
        wake.Set();
    }

    public void Pause()
    {
        lock (gate)
        {
            if (disposed || finished || paused) return;
            var frame = CurrentFrame();
            paused = true;
            Prepare(frame);
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (gate)
        {
            if (disposed || finished) return;
            var frame = Math.Max(0, Math.Min(source.Length / 4, (long)(position.TotalSeconds * 48000)));
            Prepare(frame);
            view.Write(8, paused ? 0 : 1);
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
                    view.Write(56, Stopwatch.GetTimestamp());
                    if (!paused)
                    {
                        if (!CaptureActive()) throw new IOException("Discord o l'app di acquisizione ha chiuso il microfono.");
                        var frame = CurrentFrame();
                        FillAhead(frame);
                        if (frame >= source.Length / 4)
                        {
                            baseFrame = frame;
                            paused = true;
                            finished = true;
                            view.Write(8, 0);
                            break;
                        }
                    }
                }
                wake.WaitOne(5);
            }
        }
        catch (Exception error)
        {
            failure = error;
            lock (gate) { finished = true; if (!disposed) view.Write(8, 0); }
        }
        var ended = Ended;
        if (ended != null) ended(this, new AudioPlaybackEventArgs { Error = failure });
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (view != null && worker != null) view.Write(8, 0);
        }
        wake.Set();
        if (worker != null && worker != Thread.CurrentThread) worker.Join();
        if (view != null) view.Dispose();
        if (mapping != null) mapping.Dispose();
        if (source != null) source.Dispose();
        if (writerLock != null) writerLock.Dispose();
        wake.Dispose();
    }
}
