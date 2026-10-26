using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class AudioDevice
{
    public int Number { get; set; }
    public string Name { get; set; }
    public override string ToString() { return Name; }
}

internal static class NativeAudio
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OutputCapabilities
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InputCapabilities
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct Format
    {
        public ushort Tag;
        public ushort Channels;
        public uint SampleRate;
        public uint BytesPerSecond;
        public ushort BlockAlign;
        public ushort Bits;
        public ushort Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        public IntPtr Data;
        public uint Length;
        public uint Recorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Time
    {
        public uint Type;
        public uint Value;
        public uint Reserved;
    }

    [DllImport("winmm.dll")]
    internal static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern uint waveOutGetDevCapsW(UIntPtr device, out OutputCapabilities caps, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern uint waveInGetDevCapsW(UIntPtr device, out InputCapabilities caps, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutOpen(out IntPtr handle, int device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutPause(IntPtr handle);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutRestart(IntPtr handle);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutClose(IntPtr handle);
    [DllImport("winmm.dll")]
    internal static extern uint waveOutGetPosition(IntPtr handle, ref Time position, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")]
    internal static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    internal static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")]
    internal static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")]
    internal static extern uint waveInClose(IntPtr handle);
    [DllImport("winmm.dll")]
    private static extern uint waveOutMessage(IntPtr device, uint message, out int preferred, out uint status);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetErrorTextW(uint error, StringBuilder text, uint length);

    public static AudioDevice[] PlaybackDevices()
    {
        var devices = new List<AudioDevice>();
        foreach (var index in System.Linq.Enumerable.Range(0, (int)waveOutGetNumDevs()))
        {
            OutputCapabilities caps;
            Check(waveOutGetDevCapsW(new UIntPtr((uint)index), out caps, (uint)Marshal.SizeOf(typeof(OutputCapabilities))), "Lettura dispositivi audio");
            devices.Add(new AudioDevice { Number = index, Name = caps.Name });
        }
        return devices.ToArray();
    }

    public static int PreferredPlaybackDevice()
    {
        int device;
        uint status;
        Check(waveOutMessage(new IntPtr(-1), 0x2015, out device, out status), "Lettura uscita predefinita");
        if (device < 0) throw new InvalidOperationException("Nessuna uscita audio predefinita disponibile.");
        return device;
    }

    public static string[] RecordingDevices()
    {
        var devices = new List<string>();
        foreach (var index in System.Linq.Enumerable.Range(0, (int)waveInGetNumDevs()))
        {
            InputCapabilities caps;
            Check(waveInGetDevCapsW(new UIntPtr((uint)index), out caps, (uint)Marshal.SizeOf(typeof(InputCapabilities))), "Lettura microfoni");
            devices.Add(caps.Name);
        }
        return devices.ToArray();
    }

    internal static void Check(uint result, string operation)
    {
        if (result == 0) return;
        var text = new StringBuilder(256);
        waveOutGetErrorTextW(result, text, (uint)text.Capacity);
        throw new IOException(operation + ": " + text);
    }
}
