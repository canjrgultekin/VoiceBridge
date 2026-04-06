using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Events;

public sealed class AudioDataEventArgs : EventArgs
{
    public required ReadOnlyMemory<byte> Buffer { get; init; }
    public required AudioSourceType SourceType { get; init; }
}

public sealed class AudioCaptureErrorEventArgs : EventArgs
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}

public sealed class TranscriptReceivedEventArgs : EventArgs
{
    public required TranscriptEntry Entry { get; init; }
}

public sealed class SpeechRecognitionErrorEventArgs : EventArgs
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}

public sealed class SessionStateChangedEventArgs : EventArgs
{
    public required SessionState OldState { get; init; }
    public required SessionState NewState { get; init; }
}

public sealed class TranslationCompletedEventArgs : EventArgs
{
    public required string EntryId { get; init; }
    public required string TranslatedText { get; init; }
}
