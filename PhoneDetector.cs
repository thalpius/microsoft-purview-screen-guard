using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace MicrosoftPurviewScreenGuard;

/// <summary>Detection settings. These are the values to tune.</summary>
internal static class PhoneSettings
{
    /// <summary>A frame with a phone score at or above this counts as a candidate hit.</summary>
    public const double MinScore = 0.40;

    /// <summary>One frame at or above this confirms the phone on its own.</summary>
    public const double FastScore = 0.60;

    /// <summary>Boxes smaller than this fraction of the 640x640 input are ignored.</summary>
    public const double MinAreaFraction = 0.01;

    /// <summary>The phone stays "seen" this long after the last confirmation.</summary>
    public const long HoldMs = 3000;

    /// <summary>
    /// While no sensitive document is visible the detector only gets about one frame per this many ms (CPU and battery).
    /// It must stay well below CameraHealth.DetectorStaleMs, otherwise an idle detector would count as stuck.
    /// </summary>
    public const long IdleIntervalMs = 1000;

    /// <summary>
    /// Frames with a phone score below this are not shown as a HIT line. Scores of an empty scene are around 0.0001-0.015,
    /// so anything from about 0.10 up is worth seeing (the approach of a phone). Lower it to see more, 0 shows every frame.
    /// </summary>
    public const double HitLogMinScore = 0.10;

    public const string ModelFileName = "yolov8n.onnx";
    public const int InputSize = 640;
    public const int PhoneClassId = 67;   // COCO: cell phone
    public const int OutputChannels = 84; // 4 box values + 80 class scores, no objectness in YOLOv8
}

/// <summary>Runs YOLOv8n (ONNX Runtime) on a BGR frame and returns the best cell-phone score.</summary>
internal sealed class PhoneDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float[] _input = new float[3 * PhoneSettings.InputSize * PhoneSettings.InputSize];

    private PhoneDetector(InferenceSession session, string inputName, long loadMs)
    {
        _session = session;
        _inputName = inputName;
        LoadMs = loadMs;
    }

    public long LoadMs { get; }

    /// <summary>Loads and validates the model. Throws (FileNotFoundException and others) on any problem.</summary>
    public static PhoneDetector Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"file not found: {path}");
        }

        var watch = Stopwatch.StartNew();
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };

        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(path, options);

            if (session.InputMetadata.Count != 1)
            {
                throw new InvalidOperationException($"expected 1 model input, found {session.InputMetadata.Count}");
            }

            KeyValuePair<string, NodeMetadata> input = session.InputMetadata.First();
            int[] dims = input.Value.Dimensions;
            if (input.Value.ElementDataType != TensorElementType.Float
                || dims.Length != 4
                || dims[1] != 3
                || (dims[2] != -1 && dims[2] != PhoneSettings.InputSize)
                || (dims[3] != -1 && dims[3] != PhoneSettings.InputSize))
            {
                throw new InvalidOperationException(
                    $"unexpected model input {input.Value.ElementDataType} [{string.Join(",", dims)}], expected float [1,3,{PhoneSettings.InputSize},{PhoneSettings.InputSize}]");
            }

            return new PhoneDetector(session, input.Key, watch.ElapsedMilliseconds);
        }
        catch
        {
            session?.Dispose();
            throw;
        }
        finally
        {
            options.Dispose();
        }
    }

    /// <summary>One inference on a blank frame so the first real frame is not slow. Returns the time it took in ms.</summary>
    public long WarmUp()
    {
        using var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(114));
        var watch = Stopwatch.StartNew();
        Detect(blank);
        return watch.ElapsedMilliseconds;
    }

    /// <summary>Highest cell-phone score in the frame (0 when nothing usable). Throws on anything unexpected.</summary>
    public double Detect(Mat frame)
    {
        if (frame.Empty() || frame.Channels() != 3)
        {
            throw new InvalidOperationException($"unexpected frame ({frame.Channels()} channels, empty={frame.Empty()})");
        }

        // Plain stretch to 640x640 (no letterboxing), BGR -> RGB, 0..255 -> 0..1.
        using Mat blob = CvDnn.BlobFromImage(
            frame,
            1 / 255.0,
            new OpenCvSharp.Size(PhoneSettings.InputSize, PhoneSettings.InputSize),
            default,
            swapRB: true,
            crop: false);

        if (blob.Total() != _input.Length)
        {
            throw new InvalidOperationException($"unexpected blob size {blob.Total()}, expected {_input.Length}");
        }

        Marshal.Copy(blob.Data, _input, 0, _input.Length);

        var tensor = new DenseTensor<float>(
            _input.AsMemory(),
            new[] { 1, 3, PhoneSettings.InputSize, PhoneSettings.InputSize });

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        if (results.Count < 1)
        {
            throw new InvalidOperationException("model returned no output");
        }

        Tensor<float> output = results.First().AsTensor<float>();
        ReadOnlySpan<int> shape = output.Dimensions;
        if (shape.Length != 3 || shape[0] != 1 || shape[1] != PhoneSettings.OutputChannels || shape[2] < 1)
        {
            throw new InvalidOperationException(
                $"unexpected model output shape [{string.Join(",", shape.ToArray())}], expected [1,{PhoneSettings.OutputChannels},N]");
        }

        int candidates = shape[2];
        ReadOnlySpan<float> data = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray();
        return FrameScore(data, candidates);
    }

    /// <summary>
    /// Scans a [1, 84, N] output laid out channel-major (index = channel * N + candidate).
    /// Box width/height are channels 2 and 3, the cell-phone score is channel 4 + 67.
    /// Boxes below <see cref="PhoneSettings.MinAreaFraction"/> of the input are ignored.
    /// </summary>
    internal static double FrameScore(ReadOnlySpan<float> data, int candidates)
    {
        if (data.Length != PhoneSettings.OutputChannels * candidates)
        {
            throw new InvalidOperationException($"output has {data.Length} values, expected {PhoneSettings.OutputChannels * candidates}");
        }

        float minArea = (float)(PhoneSettings.MinAreaFraction * PhoneSettings.InputSize * PhoneSettings.InputSize);
        int phoneOffset = (4 + PhoneSettings.PhoneClassId) * candidates;
        int widthOffset = 2 * candidates;
        int heightOffset = 3 * candidates;

        float best = 0;
        for (int i = 0; i < candidates; i++)
        {
            if (data[widthOffset + i] * data[heightOffset + i] < minArea)
            {
                continue;
            }

            float score = data[phoneOffset + i];
            if (float.IsNaN(score))
            {
                throw new InvalidOperationException("model produced a NaN score");
            }

            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    public void Dispose() => _session.Dispose();
}
