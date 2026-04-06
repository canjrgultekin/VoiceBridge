namespace VoiceBridge.Core.Configuration;

public sealed class VoiceBridgeOptions
{
    public const string SectionName = "VoiceBridge";

    public DeepgramOptions Deepgram { get; set; } = new();
    public DeepLOptions DeepL { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public TranslationOptions Translation { get; set; } = new();
}

public sealed class DeepgramOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "nova-3";
    public string Language { get; set; } = "multi";
    public bool Diarize { get; set; } = true;
    public bool SmartFormat { get; set; } = true;
    public bool Punctuate { get; set; } = true;
    public bool DetectLanguage { get; set; } = true;
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
