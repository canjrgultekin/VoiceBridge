namespace VoiceBridge.Core.Configuration;

public sealed class VoiceBridgeOptions
{
    public const string SectionName = "VoiceBridge";

    public DeepgramOptions Deepgram { get; set; } = new();
    public DeepLOptions DeepL { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public TranslationOptions Translation { get; set; } = new();
    public TranscriptOptions Transcript { get; set; } = new();
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

    /// <summary>
    /// Deepgram UtteranceEnd sinyali için sessizlik eşiği (ms). Bu süre boyunca konuşma
    /// olmazsa Deepgram cümlenin bittiğini varsayar. Default 2000 — uzun paragraf okumalarda
    /// doğal nefes/duraksama 1 saniyeyi rahatlıkla geçebildiği için 1000 çok agresifti.
    /// </summary>
    public int UtteranceEndMs { get; set; } = 2000;

    /// <summary>
    /// Deepgram interim → final geçişi için endpointing süresi (ms). Default 500.
    /// Deepgram'ın kendi default'u 10ms (çok agresif). 500ms ile yarım saniyelik
    /// duraksamaları cümle sonu olarak değil de doğal duraklama olarak değerlendirir.
    /// </summary>
    public int Endpointing { get; set; } = 500;

    /// <summary>
    /// Final transcript için minimum kelime sayısı. Bunun altındaki final transcript'ler
    /// (genelde tek kelimelik gürültü artefaktı) atılır. 0 = filtre yok.
    /// </summary>
    public int MinWordCount { get; set; } = 2;
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

public sealed class TranscriptOptions
{
    /// <summary>
    /// Smart merge aktif mi? Aktifse art arda gelen final transcript'ler aynı speaker'dan
    /// kısa süre içinde geliyorsa ve önceki cümle sonu noktalaması ile bitmiyorsa
    /// birleştirilir. Bu sayede konuşmacının doğal duraksamaları cümleyi bölmez.
    /// </summary>
    public bool SmartMergeEnabled { get; set; } = true;

    /// <summary>
    /// Smart merge için maksimum gap (ms). Önceki final entry'nin EndTime'ı ile yeni final
    /// entry'nin StartTime'ı arasındaki fark bu değerden küçükse merge adayı olur.
    /// Default 3000 — 3 saniyelik duraksamalar bile aynı cümle olarak kabul edilir.
    /// </summary>
    public int SmartMergeMaxGapMs { get; set; } = 3000;
}
