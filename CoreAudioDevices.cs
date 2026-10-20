using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal sealed class CaptureEndpoint
{
    public string Id { get; set; }
    public string Name { get; set; }
    public bool DefaultCommunications { get; set; }
    public override string ToString() { return Name; }
}

internal static class CoreAudioDevices
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class Enumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint state, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        void RegisterEndpointNotificationCallback(IntPtr callback);
        void UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        void Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(uint mode, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, IntPtr value);
        void Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr value);

    public static CaptureEndpoint[] RecordingDevices()
    {
        var enumerator = (IDeviceEnumerator)new Enumerator();
        IDeviceCollection collection = null;
        IDevice defaultDevice = null;
        try
        {
            var defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(1, 2, out defaultDevice) == 0) defaultDevice.GetId(out defaultId);
            enumerator.EnumAudioEndpoints(1, 1, out collection);
            uint count;
            collection.GetCount(out count);
            var result = new List<CaptureEndpoint>();
            for (uint index = 0; index < count; index++)
            {
                IDevice device;
                collection.Item(index, out device);
                try
                {
                    string id;
                    device.GetId(out id);
                    result.Add(new CaptureEndpoint { Id = id, Name = DeviceName(device), DefaultCommunications = id == defaultId });
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return result.ToArray();
        }
        finally
        {
            if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
            if (collection != null) Marshal.ReleaseComObject(collection);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string DeviceName(IDevice device)
    {
        IPropertyStore store;
        device.OpenPropertyStore(0, out store);
        var value = Marshal.AllocCoTaskMem(24);
        for (var offset = 0; offset < 24; offset++) Marshal.WriteByte(value, offset, 0);
        try
        {
            var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            store.GetValue(ref key, value);
            if (Marshal.ReadInt16(value) != 31) throw new InvalidOperationException("Nome del dispositivo audio non disponibile.");
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(value, 8));
        }
        finally
        {
            PropVariantClear(value);
            Marshal.FreeCoTaskMem(value);
            Marshal.ReleaseComObject(store);
        }
    }
}
