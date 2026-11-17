using System.Runtime.InteropServices;

namespace Jarvis;

internal static class Pcm
{
    /// <summary>Пересчёт частоты через оконный sinc-фильтр. Простая линейная интерполяция при
    /// понижении (48 кГц с микрофона → 16 кГц для Vosk) оставляет алиасинг, и распознаватель
    /// слышит шум вместо речи.</summary>
    public static short[] Resample(short[] samples, int from, int to)
    {
        if (from == to || samples.Length == 0 || from <= 0 || to <= 0) return samples;

        var ratio = (double)from / to;
        var stretch = Math.Max(1.0, ratio);
        var cutoff = 0.45 / stretch;
        var half = (int)Math.Ceiling(4.0 * stretch);
        var length = (int)((long)samples.Length * to / from) + 1;
        var output = new short[length];
        var written = 0;

        for (var i = 0; i < length; i++)
        {
            var pos = i * ratio;
            var center = (int)Math.Floor(pos);
            if (center >= samples.Length) break;

            var sum = 0.0;
            var norm = 0.0;
            for (var k = -half; k <= half; k++)
            {
                var index = center + k;
                if (index < 0 || index >= samples.Length) continue;
                var weight = Kernel(k - (pos - center), cutoff, half);
                if (weight == 0) continue;
                sum += samples[index] * weight;
                norm += weight;
            }

            output[written++] = norm <= 0 ? (short)0 : (short)Math.Clamp(sum / norm, -32768.0, 32767.0);
        }

        return written == output.Length ? output : output[..written];
    }

    /// <summary>Обрезка тишины по краям: нейросеть слышит лучше, когда в окне нет пауз до
    /// обращения и шума после фразы. Порог считаем от собственного уровня фрагмента, иначе
    /// тихий микрофон обрезался бы весь.</summary>
    public static short[] TrimSilence(short[] samples, int sampleRate)
    {
        var frame = Math.Max(1, sampleRate / 50);
        var frames = samples.Length / frame;
        if (frames < 5) return samples;

        var levels = new double[frames];
        var peak = 0.0;
        for (var at = 0; at < frames; at++)
        {
            long sum = 0;
            var start = at * frame;
            for (var i = start; i < start + frame; i++) sum += (long)samples[i] * samples[i];
            levels[at] = Math.Sqrt(sum / (double)frame);
            peak = Math.Max(peak, levels[at]);
        }

        if (peak < 200) return samples;
        var floor = Math.Max(150, peak * 0.2);

        var first = 0;
        while (first < frames && levels[first] < floor) first++;
        var last = frames - 1;
        while (last >= 0 && levels[last] < floor) last--;
        if (first > last) return samples;

        // Полсекунды запаса: слово как раз умещается в кадрах ниже порога.
        var pad = sampleRate / 5;
        var from = Math.Max(0, first * frame - pad);
        var to = Math.Min(samples.Length, (last + 1) * frame + pad);
        return samples[from..to];
    }

    private static double Kernel(double t, double cutoff, int half)
    {
        var x = t / half;
        if (x <= -1 || x >= 1) return 0;
        var window = 0.42 + 0.5 * Math.Cos(Math.PI * x) + 0.08 * Math.Cos(2 * Math.PI * x);
        return t == 0 ? 2 * cutoff * window : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t) * window;
    }

    public static short[] ReadWav(string path, out int sampleRate)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || MemoryMarshal.Read<int>(bytes) != 0x46464952) throw new InvalidDataException("Не RIFF/WAVE.");
        sampleRate = 0;
        var format = 1;
        var channels = 1;
        var bits = 16;
        var dataStart = -1;
        var dataLength = 0;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = MemoryMarshal.Read<int>(bytes.AsSpan(offset));
            var size = MemoryMarshal.Read<int>(bytes.AsSpan(offset + 4));
            var body = offset + 8;
            if (id == 0x20746d66)
            {
                format = MemoryMarshal.Read<short>(bytes.AsSpan(body));
                channels = MemoryMarshal.Read<short>(bytes.AsSpan(body + 2));
                sampleRate = MemoryMarshal.Read<int>(bytes.AsSpan(body + 4));
                bits = MemoryMarshal.Read<short>(bytes.AsSpan(body + 14));
            }
            else if (id == 0x61746164)
            {
                dataStart = body;
                dataLength = Math.Min(size, bytes.Length - body);
            }

            if (id != 0x46464952 && size < 0) break;
            offset = body + size + (size % 2);
        }

        if (dataStart < 0 || sampleRate == 0) throw new InvalidDataException("В WAV нет данных PCM.");
        if (format != 1 || bits != 16) throw new InvalidDataException($"Ожидается PCM16, получен формат {format}/{bits} бит.");

        var span = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(dataStart, dataLength - dataLength % 2));
        if (channels == 1) return span.ToArray();

        var mono = new short[span.Length / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++) sum += span[i * channels + c];
            mono[i] = (short)(sum / channels);
        }

        return mono;
    }
}
