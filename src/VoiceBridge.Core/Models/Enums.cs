namespace VoiceBridge.Core.Models;

public enum DetectedLanguage
{
    Unknown,
    Turkish,
    English
}

public enum AudioSourceType
{
    Microphone,
    SystemAudio,
    Both
}

public enum TranslationMode
{
    Realtime,
    Enhanced
}

public enum SessionState
{
    Idle,
    Connecting,
    Listening,
    Reconnecting,
    Error,
    Disconnected
}

public enum AudioDeviceChangeType
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged
}
