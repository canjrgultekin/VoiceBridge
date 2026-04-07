namespace VoiceBridge.Core.Interfaces;

/// <summary>
/// Voice Activity Detection (VAD) servisi.
/// Bir ses frame'inin insan konuşması mı yoksa gürültü/müzik mi olduğunu tespit eder.
/// </summary>
public interface IVoiceActivityDetector : IDisposable
{
    /// <summary>
    /// VAD modelinin tek bir çağrıda beklediği sample sayısı.
    /// Silero VAD v5 için 512 sample @ 16 kHz (32 ms).
    /// </summary>
    int FrameSize { get; }

    /// <summary>
    /// Beklenen sample rate (Hz). Default 16000.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Tek bir ses frame'i için konuşma olasılığını döner (0.0 - 1.0).
    /// Input: int16 PCM mono, FrameSize sample uzunluğunda.
    /// </summary>
    float GetSpeechProbability(ReadOnlySpan<short> pcm16Frame);

    /// <summary>
    /// Internal state'i sıfırlar (yeni session başlarken çağrılmalı).
    /// </summary>
    void Reset();
}
