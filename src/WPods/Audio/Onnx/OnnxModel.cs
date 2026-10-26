using System.Runtime.InteropServices;
using System.Text;

namespace WPods.Audio.Onnx;

/// <summary>
/// One ONNX Runtime session (onnxruntime.dll C API, CPU execution provider) of a streaming model whose tensors are
/// all float with static shapes (a dynamic dimension counts as 1). Every input and output gets a pinned buffer at
/// creation (<see cref="Input"/>, <see cref="Output"/>). Convention of every model used here: tensor 0 is the signal,
/// inputs 1..n are recurrent states that the next <see cref="Run"/> takes from outputs 1..n (same index, same shape;
/// the buffers are swapped, not copied). States start at zero; <see cref="State"/> exposes them for other initial
/// values. Models load from the models folder next to the exe. No allocation per <see cref="Run"/>; one caller at a time.
/// </summary>
internal sealed unsafe partial class OnnxModel : IDisposable
{
    private const string ModelFolder = "models";

    private const uint ApiVersion = 20;
    private const int LoggingLevelWarning = 2;
    private const int GraphOptimizationAll = 99;
    private const int ArenaAllocator = 1;
    private const int MemTypeDefault = 0;
    private const int ElementFloat = 1;

    // Slots in the OrtApi function table (append-only ABI, checked against onnxruntime_c_api.h of v1.31.0).
    private const int GetErrorMessageSlot = 2;
    private const int CreateEnvSlot = 3;
    private const int CreateSessionSlot = 7;
    private const int RunSlot = 9;
    private const int CreateSessionOptionsSlot = 10;
    private const int SetGraphOptimizationLevelSlot = 23;
    private const int SetIntraOpNumThreadsSlot = 24;
    private const int SetInterOpNumThreadsSlot = 25;
    private const int SessionGetInputCountSlot = 30;
    private const int SessionGetOutputCountSlot = 31;
    private const int SessionGetInputTypeInfoSlot = 33;
    private const int SessionGetOutputTypeInfoSlot = 34;
    private const int SessionGetInputNameSlot = 36;
    private const int SessionGetOutputNameSlot = 37;
    private const int CreateTensorWithDataSlot = 49;
    private const int CastTypeInfoToTensorInfoSlot = 55;
    private const int GetTensorElementTypeSlot = 60;
    private const int GetDimensionsCountSlot = 61;
    private const int GetDimensionsSlot = 62;
    private const int CreateCpuMemoryInfoSlot = 69;
    private const int AllocatorFreeSlot = 76;
    private const int GetAllocatorWithDefaultOptionsSlot = 78;
    private const int ReleaseStatusSlot = 93;
    private const int ReleaseMemoryInfoSlot = 94;
    private const int ReleaseSessionSlot = 95;
    private const int ReleaseValueSlot = 96;
    private const int ReleaseTypeInfoSlot = 98;
    private const int ReleaseSessionOptionsSlot = 100;
    private const int SessionGetModelMetadataSlot = 111;
    private const int LookupCustomMetadataMapSlot = 116;
    private const int ReleaseModelMetadataSlot = 118;
    private const int AddSessionConfigEntrySlot = 130;

    private static readonly Lock CreateLock = new();
    private static void** _api;
    private static void* _env;
    private static void* _allocator;

    private readonly byte*[] _names;
    private readonly float[][] _buffers;
    private readonly void*[] _values;
    private readonly int _inputCount;
    private readonly int _outputCount;
    private void* _session;

    /// <param name="fileName">Model file in the models folder next to the exe.</param>
    /// <param name="intraOpThreads">Threads per operator (1 = run on the caller's thread only).</param>
    public OnnxModel(string fileName, int intraOpThreads = 1)
    {
        string path = Path.Combine(AppContext.BaseDirectory, ModelFolder, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"ONNX model not found: {path}", path);
        }
        lock (CreateLock)
        {
            if (_api is null)
            {
                OrtApiBase* apiBase = OrtGetApiBase();
                void** api = (void**)apiBase->GetApi(ApiVersion);
                if (api is null)
                {
                    throw new InvalidOperationException($"onnxruntime.dll {Marshal.PtrToStringUTF8((nint)apiBase->GetVersionString())} lacks API {ApiVersion}.");
                }
                void* env;
                fixed (byte* id = "wpods"u8)
                {
                    Check(((delegate* unmanaged<int, byte*, void**, void*>)api[CreateEnvSlot])(LoggingLevelWarning, id, &env), api);
                }
                void* allocator;
                Check(((delegate* unmanaged<void**, void*>)api[GetAllocatorWithDefaultOptionsSlot])(&allocator), api);
                _env = env;
                _allocator = allocator;
                _api = api;
            }
        }

        void* options;
        Check(((delegate* unmanaged<void**, void*>)_api[CreateSessionOptionsSlot])(&options));
        try
        {
            Check(((delegate* unmanaged<void*, int, void*>)_api[SetGraphOptimizationLevelSlot])(options, GraphOptimizationAll));
            Check(((delegate* unmanaged<void*, int, void*>)_api[SetIntraOpNumThreadsSlot])(options, intraOpThreads));
            Check(((delegate* unmanaged<void*, int, void*>)_api[SetInterOpNumThreadsSlot])(options, 1));
            // Worker threads sleep between the 10 ms frames instead of spinning on a core.
            fixed (byte* key = "session.intra_op.allow_spinning"u8)
            fixed (byte* value = "0"u8)
            {
                Check(((delegate* unmanaged<void*, byte*, byte*, void*>)_api[AddSessionConfigEntrySlot])(options, key, value));
            }
            // Denormals as zero: some models (FRCRN, chunk models) run several times slower on subnormal floats.
            fixed (byte* key = "session.set_denormal_as_zero"u8)
            fixed (byte* value = "1"u8)
            {
                Check(((delegate* unmanaged<void*, byte*, byte*, void*>)_api[AddSessionConfigEntrySlot])(options, key, value));
            }
            void* session;
            fixed (char* p = path)
            {
                Check(((delegate* unmanaged<void*, char*, void*, void**, void*>)_api[CreateSessionSlot])(_env, p, options, &session));
            }
            _session = session;
        }
        finally
        {
            ((delegate* unmanaged<void*, void>)_api[ReleaseSessionOptionsSlot])(options);
        }

        try
        {
            nuint inputs;
            nuint outputs;
            Check(((delegate* unmanaged<void*, nuint*, void*>)_api[SessionGetInputCountSlot])(_session, &inputs));
            Check(((delegate* unmanaged<void*, nuint*, void*>)_api[SessionGetOutputCountSlot])(_session, &outputs));
            _inputCount = (int)inputs;
            _outputCount = (int)outputs;
            int count = _inputCount + _outputCount;
            _names = new byte*[count];
            _buffers = new float[count][];
            _values = new void*[count];

            void* memoryInfo;
            Check(((delegate* unmanaged<int, int, void**, void*>)_api[CreateCpuMemoryInfoSlot])(ArenaAllocator, MemTypeDefault, &memoryInfo));
            try
            {
                for (int i = 0; i < count; i++)
                {
                    bool input = i < _inputCount;
                    nuint index = (nuint)(input ? i : i - _inputCount);
                    byte* name;
                    Check(((delegate* unmanaged<void*, nuint, void*, byte**, void*>)_api[input ? SessionGetInputNameSlot : SessionGetOutputNameSlot])(_session, index, _allocator, &name));
                    _names[i] = name;
                    BindTensor(i, index, input, memoryInfo);
                }
            }
            finally
            {
                ((delegate* unmanaged<void*, void>)_api[ReleaseMemoryInfoSlot])(memoryInfo);
            }

            if (_inputCount != _outputCount)
            {
                throw new InvalidOperationException($"{fileName}: {_inputCount} inputs but {_outputCount} outputs.");
            }
            for (int i = 1; i < _inputCount; i++)
            {
                if (_buffers[i].Length != _buffers[_inputCount + i].Length)
                {
                    throw new InvalidOperationException($"{fileName}: state {i} input and output sizes differ.");
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Pinned buffer bound to input tensor 0 (the signal); fill it before <see cref="Run"/>.</summary>
    public float[] Input => _buffers[0];

    /// <summary>Pinned buffer bound to output tensor 0 (the signal); read it after <see cref="Run"/>.</summary>
    public float[] Output => _buffers[_inputCount];

    /// <summary>Current buffer of recurrent state input index (1..n); valid until the next <see cref="Run"/>.</summary>
    public float[] State(int index) => _buffers[index];

    /// <summary>Runs the model once, then makes the state outputs the next run's state inputs.</summary>
    public void Run()
    {
        fixed (byte** names = _names)
        fixed (void** values = _values)
        {
            Check(((delegate* unmanaged<void*, void*, byte**, void**, nuint, byte**, nuint, void**, void*>)_api[RunSlot])(
                _session, null, names, values, (nuint)_inputCount, names + _inputCount, (nuint)_outputCount, values + _inputCount));
        }
        for (int i = 1; i < _inputCount; i++)
        {
            int o = _inputCount + i;
            void* value = _values[i];
            _values[i] = _values[o];
            _values[o] = value;
            (_buffers[i], _buffers[o]) = (_buffers[o], _buffers[i]);
        }
    }

    /// <summary>Custom metadata value of the model, or null when the key is absent.</summary>
    public string? Metadata(string key)
    {
        void* metadata;
        Check(((delegate* unmanaged<void*, void**, void*>)_api[SessionGetModelMetadataSlot])(_session, &metadata));
        try
        {
            byte* value;
            byte[] utf8Key = Encoding.UTF8.GetBytes(key + "\0");
            fixed (byte* k = utf8Key)
            {
                Check(((delegate* unmanaged<void*, void*, byte*, byte**, void*>)_api[LookupCustomMetadataMapSlot])(metadata, _allocator, k, &value));
            }
            if (value is null)
            {
                return null;
            }
            string result = Marshal.PtrToStringUTF8((nint)value)!;
            ((delegate* unmanaged<void*, void*, void*>)_api[AllocatorFreeSlot])(_allocator, value);
            return result;
        }
        finally
        {
            ((delegate* unmanaged<void*, void>)_api[ReleaseModelMetadataSlot])(metadata);
        }
    }

    public void Dispose()
    {
        if (_values is not null)
        {
            for (int i = 0; i < _values.Length; i++)
            {
                if (_values[i] is not null)
                {
                    ((delegate* unmanaged<void*, void>)_api[ReleaseValueSlot])(_values[i]);
                    _values[i] = null;
                }
                if (_names[i] is not null)
                {
                    ((delegate* unmanaged<void*, void*, void*>)_api[AllocatorFreeSlot])(_allocator, _names[i]);
                    _names[i] = null;
                }
            }
        }
        if (_session is not null)
        {
            ((delegate* unmanaged<void*, void>)_api[ReleaseSessionSlot])(_session);
            _session = null;
        }
    }

    /// <summary>Reads the tensor shape of slot i and creates its pinned buffer and OrtValue.</summary>
    private void BindTensor(int i, nuint index, bool input, void* memoryInfo)
    {
        void* typeInfo;
        Check(((delegate* unmanaged<void*, nuint, void**, void*>)_api[input ? SessionGetInputTypeInfoSlot : SessionGetOutputTypeInfoSlot])(_session, index, &typeInfo));
        try
        {
            void* tensorInfo;
            Check(((delegate* unmanaged<void*, void**, void*>)_api[CastTypeInfoToTensorInfoSlot])(typeInfo, &tensorInfo));
            if (tensorInfo is null)
            {
                throw new InvalidOperationException($"ONNX tensor {i} is not a tensor.");
            }
            int type;
            Check(((delegate* unmanaged<void*, int*, void*>)_api[GetTensorElementTypeSlot])(tensorInfo, &type));
            if (type != ElementFloat)
            {
                throw new InvalidOperationException($"ONNX tensor {i} is not float.");
            }
            nuint rank;
            Check(((delegate* unmanaged<void*, nuint*, void*>)_api[GetDimensionsCountSlot])(tensorInfo, &rank));
            long* shape = stackalloc long[(int)rank];
            Check(((delegate* unmanaged<void*, long*, nuint, void*>)_api[GetDimensionsSlot])(tensorInfo, shape, rank));
            long length = 1;
            for (int d = (int)rank - 1; d >= 0; d--)
            {
                if (shape[d] < 1)
                {
                    shape[d] = 1;
                }
                length *= shape[d];
            }
            float[] buffer = GC.AllocateArray<float>((int)length, pinned: true);
            void* value;
            fixed (float* data = buffer)
            {
                Check(((delegate* unmanaged<void*, void*, nuint, long*, nuint, int, void**, void*>)_api[CreateTensorWithDataSlot])(
                    memoryInfo, data, (nuint)(length * sizeof(float)), shape, rank, ElementFloat, &value));
            }
            _buffers[i] = buffer;
            _values[i] = value;
        }
        finally
        {
            ((delegate* unmanaged<void*, void>)_api[ReleaseTypeInfoSlot])(typeInfo);
        }
    }

    private static void Check(void* status) => Check(status, _api);

    private static void Check(void* status, void** api)
    {
        if (status is null)
        {
            return;
        }
        string message = Marshal.PtrToStringUTF8((nint)((delegate* unmanaged<void*, byte*>)api[GetErrorMessageSlot])(status)) ?? "unknown error";
        ((delegate* unmanaged<void*, void>)api[ReleaseStatusSlot])(status);
        throw new InvalidOperationException($"ONNX Runtime: {message}");
    }

    [LibraryImport("onnxruntime")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static partial OrtApiBase* OrtGetApiBase();

    [StructLayout(LayoutKind.Sequential)]
    private struct OrtApiBase
    {
        public delegate* unmanaged<uint, void*> GetApi;
        public delegate* unmanaged<byte*> GetVersionString;
    }
}
