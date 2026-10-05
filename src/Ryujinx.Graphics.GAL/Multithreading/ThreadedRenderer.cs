using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// The ThreadedRenderer is a layer that can be put in front of any Renderer backend to make
    /// its processing happen on a separate thread, rather than intertwined with the GPU emulation.
    /// A new thread is created to handle the GPU command processing, separate from the renderer thread.
    /// Calls to the renderer, pipeline and resources are queued to happen on the renderer thread.
    /// </summary>
    public class ThreadedRenderer : IRenderer
    {
        private const int SpanPoolBytes = 8 * 1024 * 1024;
        private const int MaxRefsPerCommand = 2;
        private const int QueueCount = 10000;
        private const int ConsumerBatchSize = 64;

        private readonly int _elementSize;
        private readonly IRenderer _baseRenderer;
        private Thread _gpuThread;
        private Thread _backendThread;
        private bool _running;

        private readonly AutoResetEvent _frameComplete = new(true);

        private readonly ManualResetEventSlim _galWorkAvailable;
        private readonly CircularSpanPool _spanPool;

        private readonly ManualResetEventSlim _invokeRun;
        private readonly AutoResetEvent _interruptRun;

        // NEOV2 Etapa 3 (portado do Gezinjinx): o produtor espera por evento quando a fila enche,
        // no lugar de Thread.Sleep(1).
        private readonly AutoResetEvent _queueSpaceAvailable;
        private int _producerWaiting;

        // NEOV2 QUEUE-HYST1: com a fila cheia, o produtor so volta quando houver este numero de
        // posicoes livres, e o consumidor sinaliza uma unica vez por espera.
        private const int QueueResumeFreeSlots = QueueCount / 4;

        private readonly bool _queueHysteresis = Ryujinx.Common.Diagnostics.NeoGraphicsSettings.QueueHysteresisEnabled;

        private bool _lastSampleCounterClear = true;

        private readonly byte[] _commandQueue;
        private readonly object[] _refQueue;

        private int _consumerPtr;
        private int _commandCount;

        private long _queueTelemetryWraps;
        private long _queueFullEpisodes;
        private long _consumerWaits;
        private long _consumerEmptyDrains;
        private long _invokeWaits;
        private int _queueMaxDepth;
        private readonly long[] _commandHistogram = new long[256];
        private long _commandsConsumed;

        private int _producerPtr;
        private int _lastProducedPtr;
        private int _invokePtr;

        private int _refProducerPtr;
        private int _refConsumerPtr;

        public uint ProgramCount { get; set; } = 0;

        private Action _interruptAction;
        private readonly Lock _interruptLock = new();

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        internal BufferMap Buffers { get; }
        internal SyncMap Sync { get; }
        internal CircularSpanPool SpanPool { get; }
        internal ProgramQueue Programs { get; }

        public IPipeline Pipeline { get; }
        public IWindow Window { get; }

        public IRenderer BaseRenderer => _baseRenderer;

        public bool PreferUnconditionalRenderingOverReadback => _baseRenderer.PreferUnconditionalRenderingOverReadback;

        public bool PreferThreading => _baseRenderer.PreferThreading;

        public ThreadedRenderer(IRenderer renderer)
        {
            _baseRenderer = renderer;

            renderer.ScreenCaptured += (_, info) => ScreenCaptured?.Invoke(this, info);
            renderer.SetInterruptAction(Interrupt);

            Pipeline = new ThreadedPipeline(this);
            Window = new ThreadedWindow(this, renderer);
            Buffers = new BufferMap();
            Sync = new SyncMap();
            Programs = new ProgramQueue(renderer);

            _galWorkAvailable = new ManualResetEventSlim(false);
            _invokeRun = new ManualResetEventSlim();
            _interruptRun = new AutoResetEvent(false);
            _queueSpaceAvailable = new AutoResetEvent(false);
            _spanPool = new CircularSpanPool(this, SpanPoolBytes);
            SpanPool = _spanPool;

            _elementSize = BitUtils.AlignUp(CommandHelper.GetMaxCommandSize(), 4);

            _commandQueue = new byte[_elementSize * QueueCount];
            _refQueue = new object[MaxRefsPerCommand * QueueCount];
        }

        public void RunLoop(ThreadStart gpuLoop)
        {
            _running = true;

            _backendThread = Thread.CurrentThread;

            _gpuThread = new Thread(() =>
            {
                BackendThreadAffinity.PinCurrentThreadToGpuCores();
                gpuLoop();
            })
            {
                Name = "GPU.MainThread",
            };

            _gpuThread.Start();

            // NEOV2 GPU-AFFINITY2: keep the backend away from a unique prime core
            // reserved for GPU.MainThread.
            BackendThreadAffinity.PinCurrentThreadToBackendCores();

            RenderLoop();
        }

        public void RenderLoop()
        {
            // Power through the render queue until the Gpu thread work is done.

            while (_running)
            {
                _consumerWaits++;
                _galWorkAvailable.Wait();
                _galWorkAvailable.Reset();

                if (Volatile.Read(ref _interruptAction) != null)
                {
                    _interruptAction?.Invoke();
                    _interruptRun.Set();

                    Interlocked.Exchange(ref _interruptAction, null);
                }

                // The other thread can only increase the command count.
                // We can assume that if it is above 0, it will stay there or get higher.

                while (Volatile.Read(ref _commandCount) > 0 && Volatile.Read(ref _interruptAction) == null)
                {
                    // NEOV2 GAL-CMDBATCH1: publish queue progress once for a small group of
                    // commands instead of issuing a full-barrier atomic decrement for every
                    // command. Interrupts are still checked between individual commands.
                    int available = Math.Min(Volatile.Read(ref _commandCount), ConsumerBatchSize);
                    int commandPtr = _consumerPtr;
                    int processed = 0;

                    while (processed < available && Volatile.Read(ref _interruptAction) == null)
                    {
                        Span<byte> command = new(_commandQueue, commandPtr * _elementSize, _elementSize);

                        CommandType commandType = (CommandType)command[^1];
                        _commandHistogram[(byte)commandType]++;
                        long commandsConsumed = ++_commandsConsumed;

                        if (commandsConsumed % 1000000 == 0)
                        {
                            LogCommandHistogram(commandsConsumed);
                        }

                        // Run the command.

                        CommandHelper.RunCommand(command, this, _baseRenderer);

                        // Invoke commands are rare. Avoid an exclusive ARM64 read/modify/write
                        // on the overwhelmingly common asynchronous command path.
                        if (Volatile.Read(ref _invokePtr) == commandPtr &&
                            Interlocked.CompareExchange(ref _invokePtr, -1, commandPtr) == commandPtr)
                        {
                            _invokeRun.Set();
                        }

                        commandPtr++;

                        if (commandPtr == QueueCount)
                        {
                            commandPtr = 0;
                        }

                        processed++;
                    }

                    // Publish the consumed slots before reducing the count. Interlocked.Add is
                    // the release barrier observed by producers waiting for queue space.
                    Volatile.Write(ref _consumerPtr, commandPtr);
                    int remaining = Interlocked.Add(ref _commandCount, -processed);

                    if (remaining == 0)
                    {
                        _consumerEmptyDrains++;
                    }

                    if (_queueHysteresis)
                    {
                        if (Volatile.Read(ref _producerWaiting) != 0 &&
                            QueueCount - 1 - remaining >= QueueResumeFreeSlots &&
                            Interlocked.Exchange(ref _producerWaiting, 0) != 0)
                        {
                            _queueSpaceAvailable.Set();
                        }
                    }
                    else if (Volatile.Read(ref _producerWaiting) != 0)
                    {
                        _queueSpaceAvailable.Set();
                    }
                }
            }
        }

        private void LogCommandHistogram(long total)
        {
            Span<int> topIndices = stackalloc int[12];
            Span<long> topCounts = stackalloc long[12];
            topIndices.Fill(-1);

            for (int index = 0; index < _commandHistogram.Length; index++)
            {
                long count = _commandHistogram[index];

                for (int position = 0; position < topCounts.Length; position++)
                {
                    if (count > topCounts[position])
                    {
                        for (int shift = topCounts.Length - 1; shift > position; shift--)
                        {
                            topCounts[shift] = topCounts[shift - 1];
                            topIndices[shift] = topIndices[shift - 1];
                        }

                        topCounts[position] = count;
                        topIndices[position] = index;
                        break;
                    }
                }
            }

            string top = string.Empty;

            for (int i = 0; i < topIndices.Length && topIndices[i] >= 0; i++)
            {
                top += $"{(CommandType)topIndices[i]}={topCounts[i]} ";
            }

            Ryujinx.Common.Logging.Logger.Info?.Print(
                Ryujinx.Common.Logging.LogClass.Gpu,
                $"[NEOV2-GAL-COMMAND-PROFILE1] total={total} top={top}");
        }

        internal SpanRef<T> CopySpan<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            return _spanPool.Insert(data);
        }

        private TableRef<T> Ref<T>(T reference)
        {
            return new TableRef<T>(this, reference);
        }

        internal unsafe T* New<T>() where T : unmanaged, IGALCommand
        {
            if (!_queueHysteresis)
            {
                while (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
                {
                    Interlocked.Exchange(ref _producerWaiting, 1);

                    if (_producerPtr != (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
                    {
                        break;
                    }

                    _queueSpaceAvailable.WaitOne(1);
                }

                Volatile.Write(ref _producerWaiting, 0);
            }
            else if (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
            {
                _queueFullEpisodes++;

                // If incrementing the producer pointer would overflow, we need to wait.
                // _consumerPtr can only move forward, so there's no race to worry about here.

                // NEOV2 QUEUE-HYST1: espera ate haver QueueResumeFreeSlots posicoes livres,
                // em vez de voltar a cada posicao liberada.
                while (FreeQueueSlots() < QueueResumeFreeSlots)
                {
                    Interlocked.Exchange(ref _producerWaiting, 1);

                    if (FreeQueueSlots() >= QueueResumeFreeSlots)
                    {
                        break;
                    }

                    _queueSpaceAvailable.WaitOne(1);
                }

                Volatile.Write(ref _producerWaiting, 0);
            }

            int taken = _producerPtr;
            _lastProducedPtr = taken;

            _producerPtr = (_producerPtr + 1) % QueueCount;

            if (_producerPtr == 0)
            {
                long wraps = ++_queueTelemetryWraps;

                if (wraps % 100 == 0)
                {
                    Ryujinx.Common.Logging.Logger.Info?.Print(
                        Ryujinx.Common.Logging.LogClass.Gpu,
                        $"[NEOV2-GAL-QUEUE-PROFILE1] commands={wraps * QueueCount} depth={Volatile.Read(ref _commandCount)} " +
                        $"maxDepth={_queueMaxDepth} fullEpisodes={_queueFullEpisodes} consumerWaits={_consumerWaits} " +
                        $"emptyDrains={_consumerEmptyDrains} invokeWaits={_invokeWaits}");
                }
            }

            Span<byte> memory = new(_commandQueue, taken * _elementSize, _elementSize);
            T* result = (T*)Unsafe.AsPointer(ref memory.GetPinnableReference());
            // ref T result = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(memory));

            memory[^1] = (byte)(result)->CommandType;

            return result;
        }

        // NEOV2 QUEUE-HYST1: posicoes livres na fila, vistas pelo produtor.
        private int FreeQueueSlots()
        {
            int used = (_producerPtr - Volatile.Read(ref _consumerPtr) + QueueCount) % QueueCount;

            return QueueCount - 1 - used;
        }

        internal int AddTableRef(object obj)
        {
            // The reference table is sized so that it will never overflow, so long as the references are taken after the command is allocated.

            // NEOV2 Etapa 9 (PR #174 do Ryubing): reserva a posicao de forma atomica. Antes, duas threads
            // podiam ler o mesmo _refProducerPtr e gravar no mesmo slot, perdendo uma das referencias.
            int index;
            int next;

            do
            {
                index = Volatile.Read(ref _refProducerPtr);
                next = (index + 1) % _refQueue.Length;
            }
            while (Interlocked.CompareExchange(ref _refProducerPtr, next, index) != index);

            _refQueue[index] = obj;

            return index;
        }

        internal object RemoveTableRef(int index)
        {
            Debug.Assert(index == _refConsumerPtr);

            object result = _refQueue[_refConsumerPtr];
            _refQueue[_refConsumerPtr] = null;

            _refConsumerPtr = (_refConsumerPtr + 1) % _refQueue.Length;

            return result;
        }

        internal void QueueCommand()
        {
            int result = Interlocked.Increment(ref _commandCount);

            if (result > _queueMaxDepth)
            {
                _queueMaxDepth = result;
            }

            if (result == 1)
            {
                _galWorkAvailable.Set();
            }
        }

        internal void InvokeCommand()
        {
            _invokeWaits++;
            _invokeRun.Reset();
            _invokePtr = _lastProducedPtr;

            QueueCommand();

            // Wait for the command to complete.
            _invokeRun.Wait();
        }

        internal void WaitForFrame()
        {
            _frameComplete.WaitOne();
        }

        internal void SignalFrame()
        {
            _frameComplete.Set();
        }

        internal bool IsGpuThread()
        {
            return Thread.CurrentThread == _gpuThread;
        }

        public unsafe void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            if (IsGpuThread() && !alwaysBackground)
            {
                // The action must be performed on the render thread.
                New<ActionCommand>()->Set(Ref(action));
                InvokeCommand();
            }
            else
            {
                _baseRenderer.BackgroundContextAction(action, true);
            }
        }

        public unsafe BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferAccessCommand>()->Set(handle, size, access);
            QueueCommand();

            return handle;
        }

        public unsafe BufferHandle CreateBuffer(nint pointer, int size)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateHostBufferCommand>()->Set(handle, pointer, size);
            QueueCommand();

            return handle;
        }

        public unsafe BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferSparseCommand>()->Set(handle, CopySpan(storageBuffers));
            QueueCommand();

            return handle;
        }

        public unsafe IImageArray CreateImageArray(int size, bool isBuffer)
        {
            ThreadedImageArray imageArray = new(this);
            New<CreateImageArrayCommand>()->Set(Ref(imageArray), size, isBuffer);
            QueueCommand();

            return imageArray;
        }

        public unsafe IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            ThreadedProgram program = new(this);

            SourceProgramRequest request = new(program, shaders, info);

            Programs.Add(request);

            ProgramCount++;

            New<CreateProgramCommand>()->Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public unsafe ISampler CreateSampler(SamplerCreateInfo info)
        {
            ThreadedSampler sampler = new(this);
            New<CreateSamplerCommand>()->Set(Ref(sampler), info);
            QueueCommand();

            return sampler;
        }

        public unsafe void CreateSync(ulong id, bool strict)
        {
            Sync.CreateSyncHandle(id);
            New<CreateSyncCommand>()->Set(id, strict);
            QueueCommand();
        }

        public unsafe ITexture CreateTexture(TextureCreateInfo info)
        {
            if (IsGpuThread())
            {
                ThreadedTexture texture = new(this, info);
                New<CreateTextureCommand>()->Set(Ref(texture), info);
                QueueCommand();

                return texture;
            }
            else
            {
                ThreadedTexture texture = new(this, info)
                {
                    Base = _baseRenderer.CreateTexture(info),
                };

                return texture;
            }
        }
        public unsafe ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            ThreadedTextureArray textureArray = new(this);
            New<CreateTextureArrayCommand>()->Set(Ref(textureArray), size, isBuffer);
            QueueCommand();

            return textureArray;
        }

        public unsafe void DeleteBuffer(BufferHandle buffer)
        {
            New<BufferDisposeCommand>()->Set(buffer);
            QueueCommand();
        }

        public unsafe PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            if (IsGpuThread())
            {
                ResultBox<PinnedSpan<byte>> box = new();
                New<BufferGetDataCommand>()->Set(buffer, offset, size, Ref(box));
                InvokeCommand();

                return box.Result;
            }
            else
            {
                return _baseRenderer.GetBufferData(Buffers.MapBufferBlocking(buffer), offset, size);
            }
        }

        public unsafe Capabilities GetCapabilities()
        {
            ResultBox<Capabilities> box = new();
            New<GetCapabilitiesCommand>()->Set(Ref(box));
            InvokeCommand();

            return box.Result;
        }

        public ulong GetCurrentSync()
        {
            return _baseRenderer.GetCurrentSync();
        }

        public HardwareInfo GetHardwareInfo()
        {
            return _baseRenderer.GetHardwareInfo();
        }

        /// <summary>
        /// Initialize the base renderer. Must be called on the render thread.
        /// </summary>
        /// <param name="logLevel">Log level to use</param>
        public void Initialize(GraphicsDebugLevel logLevel)
        {
            _baseRenderer.Initialize(logLevel);
        }

        public unsafe IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            ThreadedProgram program = new(this);

            BinaryProgramRequest request = new(program, programBinary, hasFragmentShader, info);
            Programs.Add(request);

            New<CreateProgramCommand>()->Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public unsafe void PreFrame()
        {
            New<PreFrameCommand>();
            QueueCommand();
        }

        public unsafe ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            ThreadedCounterEvent evt = new(this, type, _lastSampleCounterClear);
            New<ReportCounterCommand>()->Set(Ref(evt), type, Ref(resultHandler), divisor, hostReserved);
            QueueCommand();

            if (type == CounterType.SamplesPassed)
            {
                _lastSampleCounterClear = false;
            }

            return evt;
        }

        public unsafe void ResetCounter(CounterType type)
        {
            New<ResetCounterCommand>()->Set(type);
            QueueCommand();
            _lastSampleCounterClear = true;
        }

        public void Screenshot()
        {
            _baseRenderer.Screenshot();
        }

        public unsafe void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            New<BufferSetDataCommand>()->Set(buffer, offset, CopySpan(data));
            QueueCommand();
        }

        public unsafe void UpdateCounters()
        {
            New<UpdateCountersCommand>();
            QueueCommand();
        }

        public void WaitSync(ulong id)
        {
            Sync.WaitSyncAvailability(id);

            _baseRenderer.WaitSync(id);
        }

        private void Interrupt(Action action)
        {
            // Interrupt the backend thread from any external thread and invoke the given action.

            if (Thread.CurrentThread == _backendThread)
            {
                // If this is called from the backend thread, the action can run immediately.
                action();
            }
            else
            {
                lock (_interruptLock)
                {
                    while (Interlocked.CompareExchange(ref _interruptAction, action, null) != null)
                    {
                    }

                    _galWorkAvailable.Set();

                    _interruptRun.WaitOne();
                }
            }
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            // Threaded renderer ignores given interrupt action, as it provides its own to the child renderer.
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return _baseRenderer.PrepareHostMapping(address, size);
        }

        public void FlushThreadedCommands()
        {
            SpinWait wait = new();

            while (Volatile.Read(ref _commandCount) > 0)
            {
                wait.SpinOnce();
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            // Dispose must happen from the render thread, after all commands have completed.

            // Stop the GPU thread.
            _running = false;
            _galWorkAvailable.Set();

            if (_gpuThread != null && _gpuThread.IsAlive)
            {
                _gpuThread.Join();
            }

            // Dispose the renderer.
            _baseRenderer.Dispose();

            // Dispose events.
            _frameComplete.Dispose();
            _galWorkAvailable.Dispose();
            _invokeRun.Dispose();
            _interruptRun.Dispose();
            _queueSpaceAvailable.Dispose();

            Sync.Dispose();
        }
    }
}
