using System;
using System.Linq;

internal enum AudioRoute { Speakers, Microphone, Both }

internal static class AudioRouting
{
    public static AudioDevice[] VirtualDevices()
    {
        return NativeAudio.PlaybackDevices().Where(device =>
            device.Name.IndexOf("Voicemeeter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            device.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 ||
            device.Name.IndexOf("Virtual Cable", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(device => device.Name.StartsWith("Voicemeeter Input (", StringComparison.OrdinalIgnoreCase) ? 0 : device.Name.StartsWith("Voicemeeter AUX Input", StringComparison.OrdinalIgnoreCase) ? 1 : device.Name.StartsWith("Voicemeeter VAIO3 Input", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenBy(device => device.Number).ToArray();
    }

    public static int[] Resolve(AudioRoute route, string microphoneName)
    {
        if (route == AudioRoute.Speakers) return new[] { NativeAudio.PreferredPlaybackDevice() };
        var devices = VirtualDevices().Where(device => string.Equals(device.Name, microphoneName, StringComparison.Ordinal)).ToArray();
        if (devices.Length != 1) throw new InvalidOperationException("Seleziona un dispositivo disponibile in Riproduci > Dispositivo microfono virtuale.");
        return route == AudioRoute.Microphone ? new[] { devices[0].Number } : new[] { NativeAudio.PreferredPlaybackDevice(), devices[0].Number }.Distinct().ToArray();
    }
}
