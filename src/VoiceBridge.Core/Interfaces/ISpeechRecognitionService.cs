using VoiceBridge.Core.Events;
using VoiceBridge.Core.Models;

namespace VoiceBridge.Core.Interfaces;

public interface ISpeechRecognitionService : IAsyncDisposable
{
    event EventHandler<TranscriptReceivedEventArgs>? TranscriptReceived;
    event EventHandler<SpeechRecognitionErrorEventArgs>? RecognitionError;
    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken ct = default);
    Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    SessionState CurrentState { get; }
}
