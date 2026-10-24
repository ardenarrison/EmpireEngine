using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Engine.Core.Audio;
using Hexa.NET.MiniAudio;

namespace Engine.Brutal.Audio;

// Opens the default sound device with miniaudio and feeds it from AudioEngine.Mix. Create one when the program starts
// and dispose it when it ends. A machine with no sound device, or without the native library, runs silent: the
// constructor says so on the console instead of throwing.
public sealed unsafe class AudioOutput : IDisposable
{
    private MaDevice* _device;

    public bool IsRunning { get; private set; }

    public AudioOutput()
    {
        try
        {
            var config = MiniAudio.DeviceConfigInit(MaDeviceType.Playback);
            config.Playback.Format = MaFormat.F32;
            config.Playback.Channels = 2;
            config.SampleRate = 0;   // the device's own rate, so there is no extra conversion
            config.DataCallback = (void*)(delegate* unmanaged[Cdecl]<MaDevice*, void*, void*, uint, void>)&OnAudio;

            _device = (MaDevice*)NativeMemory.AllocZeroed((nuint)sizeof(MaDevice));
            var result = MiniAudio.DeviceInit(null, &config, _device);
            if (result != MaResult.Success)
            {
                Console.WriteLine($"Audio is off: no sound device could be opened ({result}).");
                Free();
                return;
            }
            AudioEngine.SampleRate = (int)_device->SampleRate;

            result = MiniAudio.DeviceStart(_device);
            if (result != MaResult.Success)
            {
                Console.WriteLine($"Audio is off: the sound device would not start ({result}).");
                MiniAudio.DeviceUninit(_device);
                Free();
                return;
            }
            IsRunning = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"Audio is off: the sound library could not be loaded ({ex.Message}).");
            Free();
        }
    }

    // Runs on the sound device's own thread, many times a second. It must never throw.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAudio(MaDevice* device, void* output, void* input, uint frames)
    {
        var buffer = new Span<float>(output, (int)frames * 2);
        try
        {
            AudioEngine.Mix(buffer, (int)frames);
        }
        catch
        {
            buffer.Clear();
        }
    }

    private void Free()
    {
        if (_device is not null)
            NativeMemory.Free(_device);
        _device = null;
    }

    public void Dispose()
    {
        if (_device is null) return;
        if (IsRunning)
        {
            MiniAudio.DeviceUninit(_device);
            IsRunning = false;
        }
        Free();
    }
}
