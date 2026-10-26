using System;
using System.Runtime.InteropServices;
using System.Threading;

// Copia il microfono predefinito di Windows verso un'uscita virtuale (es. CABLE Input).
// Il motore audio di Windows somma questo flusso ai suoni riprodotti dal player sulla stessa uscita.
internal sealed class MicrophonePassthrough : IDisposable
{
    private const int BufferCount = 4;
    private const int BlockBytes = 3840; // 20 ms, stereo 16 bit a 48 kHz
    private const uint WhdrDone = 1;
    private const uint WhdrInQueue = 16;

    private readonly object gate = new object();
    private readonly int outputDevice;
    private readonly uint headerSize = (uint)Marshal.SizeOf(typeof(NativeAudio.Header));
    private readonly int flagsOffset = (int)Marshal.OffsetOf(typeof(NativeAudio.Header), "Flags");
    private readonly int recordedOffset = (int)Marshal.OffsetOf(typeof(NativeAudio.Header), "Recorded");
    private readonly IntPtr[] inputHeaders = new IntPtr[BufferCount];
    private readonly IntPtr[] outputHeaders = new IntPtr[BufferCount];
    private readonly IntPtr[] inputData = new IntPtr[BufferCount];
    private readonly IntPtr[] outputData = new IntPtr[BufferCount];
    private IntPtr inputHandle;
    private IntPtr outputHandle;
    private Thread worker;
    private volatile bool running;
    private Exception failure;

    public event EventHandler<EventArgs> Stopped;
    public Exception Failure { get { lock (gate) return failure; } }

    public MicrophonePassthrough(int outputDevice)
    {
        this.outputDevice = outputDevice;
    }

    public void Start()
    {
        var format = new NativeAudio.Format { Tag = 1, Channels = 2, SampleRate = 48000, BytesPerSecond = 192000, BlockAlign = 4, Bits = 16 };
        try
        {
            // WAVE_MAPPER: microfono predefinito di Windows per la registrazione
            NativeAudio.Check(NativeAudio.waveInOpen(out inputHandle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0), "Apertura microfono");
            NativeAudio.Check(NativeAudio.waveOutOpen(out outputHandle, outputDevice, ref format, IntPtr.Zero, IntPtr.Zero, 0), "Apertura uscita virtuale");
            for (var index = 0; index < BufferCount; index++)
            {
                inputData[index] = Marshal.AllocHGlobal(BlockBytes);
                outputData[index] = Marshal.AllocHGlobal(BlockBytes);
                inputHeaders[index] = NewHeader(inputData[index]);
                outputHeaders[index] = NewHeader(outputData[index]);
                NativeAudio.Check(NativeAudio.waveInPrepareHeader(inputHandle, inputHeaders[index], headerSize), "Preparazione microfono");
                NativeAudio.Check(NativeAudio.waveOutPrepareHeader(outputHandle, outputHeaders[index], headerSize), "Preparazione uscita virtuale");
                NativeAudio.Check(NativeAudio.waveInAddBuffer(inputHandle, inputHeaders[index], headerSize), "Lettura microfono");
            }
            NativeAudio.Check(NativeAudio.waveInStart(inputHandle), "Avvio microfono");
            running = true;
            worker = new Thread(Pump) { IsBackground = true, Name = "Soundboard microfono" };
            worker.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private IntPtr NewHeader(IntPtr data)
    {
        var header = Marshal.AllocHGlobal((int)headerSize);
        Marshal.StructureToPtr(new NativeAudio.Header { Data = data, Length = BlockBytes }, header, false);
        return header;
    }

    // Ogni blocco registrato viene copiato nel blocco d'uscita con lo stesso indice.
    // Se l'uscita e' ancora occupata il blocco viene scartato: meglio perdere 20 ms che accumulare ritardo.
    private void Pump()
    {
        Exception error = null;
        try
        {
            while (running)
            {
                for (var index = 0; index < BufferCount; index++)
                {
                    var flags = (uint)Marshal.ReadInt32(inputHeaders[index], flagsOffset);
                    if ((flags & WhdrDone) == 0) continue;
                    var recorded = Marshal.ReadInt32(inputHeaders[index], recordedOffset);
                    var outputFlags = (uint)Marshal.ReadInt32(outputHeaders[index], flagsOffset);
                    if (recorded > 0 && (outputFlags & WhdrInQueue) == 0)
                    {
                        var block = new byte[recorded];
                        Marshal.Copy(inputData[index], block, 0, recorded);
                        Marshal.Copy(block, 0, outputData[index], recorded);
                        Marshal.WriteInt32(outputHeaders[index], IntPtr.Size, recorded);
                        NativeAudio.Check(NativeAudio.waveOutWrite(outputHandle, outputHeaders[index], headerSize), "Invio al microfono virtuale");
                    }
                    NativeAudio.Check(NativeAudio.waveInAddBuffer(inputHandle, inputHeaders[index], headerSize), "Lettura microfono");
                }
                Thread.Sleep(2);
            }
        }
        catch (Exception failed) { error = failed; }
        lock (gate) failure = error;
        running = false;
        var stopped = Stopped;
        if (stopped != null) stopped(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        running = false;
        if (worker != null && worker != Thread.CurrentThread) worker.Join();
        worker = null;
        if (inputHandle != IntPtr.Zero) NativeAudio.waveInReset(inputHandle);
        if (outputHandle != IntPtr.Zero) NativeAudio.waveOutReset(outputHandle);
        for (var index = 0; index < BufferCount; index++)
        {
            if (inputHeaders[index] != IntPtr.Zero)
            {
                if (inputHandle != IntPtr.Zero) NativeAudio.waveInUnprepareHeader(inputHandle, inputHeaders[index], headerSize);
                Marshal.FreeHGlobal(inputHeaders[index]);
                inputHeaders[index] = IntPtr.Zero;
            }
            if (outputHeaders[index] != IntPtr.Zero)
            {
                if (outputHandle != IntPtr.Zero) NativeAudio.waveOutUnprepareHeader(outputHandle, outputHeaders[index], headerSize);
                Marshal.FreeHGlobal(outputHeaders[index]);
                outputHeaders[index] = IntPtr.Zero;
            }
            if (inputData[index] != IntPtr.Zero) { Marshal.FreeHGlobal(inputData[index]); inputData[index] = IntPtr.Zero; }
            if (outputData[index] != IntPtr.Zero) { Marshal.FreeHGlobal(outputData[index]); outputData[index] = IntPtr.Zero; }
        }
        if (inputHandle != IntPtr.Zero) { NativeAudio.waveInClose(inputHandle); inputHandle = IntPtr.Zero; }
        if (outputHandle != IntPtr.Zero) { NativeAudio.waveOutClose(outputHandle); outputHandle = IntPtr.Zero; }
    }
}
