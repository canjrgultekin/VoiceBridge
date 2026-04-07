namespace VoiceBridge.Core.Configuration;

public sealed class VoiceBridgeOptions
{
    public const string SectionName = "VoiceBridge";

    public DeepgramOptions Deepgram { get; set; } = new();
    public DeepLOptions DeepL { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public TranslationOptions Translation { get; set; } = new();
    public AudioFilteringOptions AudioFiltering { get; set; } = new();
}

public sealed class DeepgramOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "nova-3";

    // Dil modu:
    // "tr"    = sadece Türkçe
    // "en"    = sadece İngilizce
    // "tr-en" = Dual stream (Türkçe primary + İngilizce secondary)
    // "en-tr" = Dual stream (İngilizce primary + Türkçe secondary)
    // "multi" = Deepgram multilingual code-switching (Türkçe desteği sınırlı)
    public string Language { get; set; } = "tr-en";

    public bool Diarize { get; set; } = true;
    public bool SmartFormat { get; set; } = true;
    public bool Punctuate { get; set; } = true;
    public int SampleRate { get; set; } = 16000;
    public string Encoding { get; set; } = "linear16";
    public int Channels { get; set; } = 1;
    public bool InterimResults { get; set; } = true;
    public bool UtteranceEnd { get; set; } = true;
    public int UtteranceEndMs { get; set; } = 1000;
}

public sealed class DeepLOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ModelType { get; set; } = "latency_optimized";
    public string Formality { get; set; } = "default";
}

public sealed class AudioOptions
{
    public int SampleRate { get; set; } = 16000;
    public int BitsPerSample { get; set; } = 16;
    public int ChannelCount { get; set; } = 1;
    public int BufferMs { get; set; } = 20;
}

public sealed class TranslationOptions
{
    public bool AutoTranslate { get; set; } = true;
    public int BatchDelayMs { get; set; } = 100;
    public int MaxBatchSize { get; set; } = 5;
}

/// <summary>
/// Ses ön-filtreleme ayarları: VAD, noise gate, speaker confidence.
/// Ortamda TV/müzik/gürültü olduğunda yanlış transcript oluşmasını engeller
/// ve speaker diarization kalitesini artırır.
/// </summary>
public sealed class AudioFilteringOptions
{
    /// <summary>
    /// Silero VAD ile ses aktivitesi tespiti aktif mi?
    /// Kapalıysa tüm ses Deepgram'e gönderilir (eski davranış).
    /// </summary>
    public bool EnableVad { get; set; } = true;

    /// <summary>
    /// Silero VAD speech probability eşiği (0.0 - 1.0).
    /// Bu değerin altındaki chunk'lar gürültü sayılır ve Deepgram'e gönderilmez.
    /// Düşük değer = daha hassas (gürültüyü bile konuşma sanır).
    /// Yüksek değer = daha seçici (sadece net konuşmayı geçirir).
    /// </summary>
    public float VadThreshold { get; set; } = 0.5f;

    /// <summary>
    /// VAD kapandıktan sonra kaç ms boyunca konuşma devam ediyor sayılsın?
    /// Kelime aralarındaki doğal duraksamaları yutmak için.
    /// </summary>
    public int VadHangoverMs { get; set; } = 400;

    /// <summary>
    /// RMS noise gate aktif mi? Silero VAD'e ek bir enerji filtresi.
    /// </summary>
    public bool EnableNoiseGate { get; set; } = true;

    /// <summary>
    /// Noise gate RMS eşiği (dBFS). Bu değerin altındaki sesler bastırılır.
    /// -60 dBFS çok sessiz, -30 dBFS orta, -20 dBFS agresif.
    /// Default: -45 dBFS (arka plan TV/uzak sesi keser ama yakın konuşmayı bozmaz).
    /// </summary>
    public double NoiseGateDbfs { get; set; } = -45.0;

    /// <summary>
    /// Bir transcript için minimum kelime sayısı. Bunun altındaki final transcript'ler
    /// (genelde tek kelimelik gürültü artefaktı) dropped edilir.
    /// </summary>
    public int MinWordCount { get; set; } = 2;
}
