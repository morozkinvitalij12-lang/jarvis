using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Jarvis;

/// <summary>Рендер речи в PCM и её воспроизведение.</summary>
internal interface ITts : IDisposable
{
    string Name { get; }
    int SampleRate { get; }
    void Speak(string text, float gain);
    void Stop();
    short[] Render(string text);
}

internal sealed class Speaker : IDisposable
{
    private readonly object _sync = new();
    private IWavePlayer? _player;
    private WaveStream? _source;

    public void Play(short[] samples, int sampleRate, float gain)
    {
        if (samples.Length == 0) return;

        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        // PlaybackStopped приходит асинхронно, поэтому флаг не освобождаем: «замолчи» из
        // другого потока иначе зацепит обработчик уже закрытого события.
        var done = new ManualResetEventSlim();
        lock (_sync)
        {
            StopLocked();
            var source = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(sampleRate, 16, 1));
            var player = new WasapiOut(AudioClientShareMode.Shared, 120);
            _source = source;
            _player = player;
            player.PlaybackStopped += (_, _) => done.Set();
            try
            {
                player.Init(source);
                player.Play();
            }
            catch (Exception)
            {
                StopLocked();
                throw;
            }
        }

        done.Wait(TimeSpan.FromSeconds(Math.Clamp(samples.Length / (double)sampleRate * 3 + 2, 2, 90)));
        lock (_sync) StopLocked();
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
    }

    public void Dispose()
    {
        lock (_sync) StopLocked();
    }

    private void StopLocked()
    {
        var player = _player;
        var source = _source;
        _player = null;
        _source = null;
        if (player is not null)
        {
            try { player.Stop(); } catch (Exception) { }
            try { player.Dispose(); } catch (Exception) { }
        }

        try { source?.Dispose(); } catch (Exception) { }
    }
}

internal static class Wav
{
    public static void Write(string path, short[] samples, int sampleRate)
    {
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + bytes.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
