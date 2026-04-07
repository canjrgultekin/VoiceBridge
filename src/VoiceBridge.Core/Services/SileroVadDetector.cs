using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VoiceBridge.Core.Interfaces;

namespace VoiceBridge.Core.Services;

/// <summary>
/// Silero VAD v5 ONNX modelini kullanarak ses aktivitesi tespiti yapar.
/// Model, Core projesine EmbeddedResource olarak gömülüdür.
/// Thread-safe (lock ile korumalı), tek bir instance DI üzerinden singleton olarak kullanılır.
/// </summary>
public sealed class SileroVadDetector : IVoiceActivityDetector
{
    private const string ResourceName = "VoiceBridge.Core.Resources.silero_vad.onnx";
    private const int VadSampleRate = 16000;
    private const int VadFrameSize = 512; // Silero v5: 512 sample = 32 ms @ 16 kHz
    private const int StateDim = 128;
    private const int StateSize = 2 * 1 * StateDim;

    private readonly InferenceSession _session;
    private readonly ILogger<SileroVadDetector>? _logger;
    private readonly float[] _state = new float[StateSize];
    private readonly long[] _srArray = { VadSampleRate };
    private readonly object _lock = new();
    private bool _disposed;
    private long _callCount;

    public int FrameSize => VadFrameSize;
    public int SampleRate => VadSampleRate;

    public SileroVadDetector(ILogger<SileroVadDetector>? logger = null)
    {
        _logger = logger;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Silero VAD modeli bulunamadı. EmbeddedResource: {ResourceName}. " +
                "Core projesinde Resources\\silero_vad.onnx dosyası var mı?");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var modelBytes = ms.ToArray();

        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(modelBytes, options);

        // Model metadata'sını logla (input/output tensor isimleri ve shape'leri)
        try
        {
            var inputNames = string.Join(", ", _session.InputMetadata.Keys);
            var outputNames = string.Join(", ", _session.OutputMetadata.Keys);
            _logger?.LogInformation(
                "Silero VAD modeli yüklendi. Model boyut: {Size} byte. Inputs: [{Inputs}], Outputs: [{Outputs}]",
                modelBytes.Length, inputNames, outputNames);

            foreach (var kv in _session.InputMetadata)
            {
                _logger?.LogInformation(
                    "  Input '{Name}': type={Type}, shape=[{Shape}]",
                    kv.Key, kv.Value.ElementType.Name, string.Join(",", kv.Value.Dimensions));
            }
            foreach (var kv in _session.OutputMetadata)
            {
                _logger?.LogInformation(
                    "  Output '{Name}': type={Type}, shape=[{Shape}]",
                    kv.Key, kv.Value.ElementType.Name, string.Join(",", kv.Value.Dimensions));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Silero VAD model metadata loglanamadı");
        }
    }

    public float GetSpeechProbability(ReadOnlySpan<short> pcm16Frame)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SileroVadDetector));

        if (pcm16Frame.Length != VadFrameSize)
            throw new ArgumentException(
                $"Beklenen frame size {VadFrameSize}, verilen: {pcm16Frame.Length}",
                nameof(pcm16Frame));

        lock (_lock)
        {
            // int16 -> float32 normalize [-1, 1]
            var audioFloat = new float[VadFrameSize];
            double sumSq = 0;
            short maxAbs = 0;
            for (int i = 0; i < VadFrameSize; i++)
            {
                short s = pcm16Frame[i];
                audioFloat[i] = s / 32768f;
                sumSq += (double)s * s;
                short absS = s < 0 ? (short)-s : s;
                if (absS > maxAbs) maxAbs = absS;
            }
            double inputRms = Math.Sqrt(sumSq / VadFrameSize);

            float probability = 0f;
            Exception? inferenceError = null;

            try
            {
                var audioTensor = new DenseTensor<float>(audioFloat, new[] { 1, VadFrameSize });
                var stateTensor = new DenseTensor<float>(_state.ToArray(), new[] { 2, 1, StateDim });
                var srTensor = new DenseTensor<long>(_srArray, new[] { 1 });

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", audioTensor),
                    NamedOnnxValue.CreateFromTensor("state", stateTensor),
                    NamedOnnxValue.CreateFromTensor("sr", srTensor)
                };

                using var results = _session.Run(inputs);

                foreach (var r in results)
                {
                    if (r.Name == "output")
                    {
                        var tensor = r.AsTensor<float>();
                        probability = tensor.ToArray()[0];
                    }
                    else if (r.Name == "stateN")
                    {
                        var tensor = r.AsTensor<float>();
                        var newState = tensor.ToArray();
                        Array.Copy(newState, _state, Math.Min(newState.Length, _state.Length));
                    }
                }
            }
            catch (Exception ex)
            {
                inferenceError = ex;
            }

            _callCount++;

            // Debug log: ilk 15 call + sonra her 100'de bir + her hata
            bool shouldLog = _callCount <= 15 || _callCount % 100 == 0 || inferenceError != null;
            if (shouldLog && _logger != null)
            {
                if (inferenceError != null)
                {
                    _logger.LogError(inferenceError,
                        "Silero VAD inference HATASI #{Call}: inputRms={Rms:F0}, maxAbs={Max}",
                        _callCount, inputRms, maxAbs);
                }
                else
                {
                    _logger.LogInformation(
                        "Silero VAD #{Call}: prob={Prob:F4}, inputRms={Rms:F0}, maxAbs={Max}",
                        _callCount, probability, inputRms, maxAbs);
                }
            }

            return probability;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_state);
            _callCount = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }
}
