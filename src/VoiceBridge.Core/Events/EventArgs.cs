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

public sealed class AudioDeviceChangedEventArgs : EventArgs
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required AudioDeviceChangeType ChangeType { get; init; }
    public required bool AffectsCurrentSession { get; init; }
}

public sealed class AudioLevelEventArgs : EventArgs
{
    public required double Peak { get; init; }
    public required double Rms { get; init; }
    public required AudioSourceType SourceType { get; init; }
}

public sealed class TranscriptReceivedEventArgs : EventArgs
{
    public required TranscriptEntry Entry { get; init; }

    /// <summary>
    /// True ise bu yeni bir entry değil, mevcut bir entry'nin (aynı Id'li)
    /// smart-merge sonucunda güncellenmiş halidir. UI yeni satır eklemek yerine
    /// mevcut ViewModel'i Id ile bulup içeriğini güncellemelidir.
    /// </summary>
    public bool IsUpdate { get; init; }
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
    public bool Success { get; init; } = true;
    public string? ErrorMessage { get; init; }
}
