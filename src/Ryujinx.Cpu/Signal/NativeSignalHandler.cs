using ARMeilleure.Signal;
using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.Signal
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct SignalHandlerRange
    {
        public int IsActive;
        public nuint RangeAddress;
        public nuint RangeEndAddress;
        public nint ActionPointer;
    }

    [InlineArray(NativeSignalHandlerGenerator.MaxTrackedRanges)]
    struct SignalHandlerRangeArray
    {
        public SignalHandlerRange Range0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct SignalHandlerConfig
    {
        /// <summary>
        /// The byte offset of the faulting address in the SigInfo or ExceptionRecord struct.
        /// </summary>
        public int StructAddressOffset;

        /// <summary>
        /// The byte offset of the write flag in the SigInfo or ExceptionRecord struct.
        /// </summary>
        public int StructWriteOffset;

        /// <summary>
        /// The sigaction handler that was registered before this one. (unix only)
        /// </summary>
        public nuint UnixOldSigaction;

        /// <summary>
        /// The type of the previous sigaction. True for the 3 argument variant. (unix only)
        /// </summary>
        public int UnixOldSigaction3Arg;

        /// <summary>
        /// Fixed size array of tracked ranges.
        /// </summary>
        public SignalHandlerRangeArray Ranges;
    }

    public static class NativeSignalHandler
    {
        private static readonly nint _handlerConfig;
        private static nint _signalHandlerPtr;

        private static MemoryBlock _codeBlock;

        private static readonly Lock _lock = new();
        private static bool _initialized;
        private static bool _customUnixWrapperInstalled;
        private static UnixSignalHandlerRegistration.SigAction _unixOriginalAction;

        static NativeSignalHandler()
        {
            _handlerConfig = Marshal.AllocHGlobal(Unsafe.SizeOf<SignalHandlerConfig>());
            ref SignalHandlerConfig config = ref GetConfigRef();

            config = new SignalHandlerConfig();
        }

        public static void InitializeSignalHandler(Func<nint, nint, nint> customSignalHandlerFactory = null)
        {
            if (_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                int rangeStructSize = Unsafe.SizeOf<SignalHandlerRange>();

                ref SignalHandlerConfig config = ref GetConfigRef();

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
                {
                    _signalHandlerPtr = MapCode(NativeSignalHandlerGenerator.GenerateUnixSignalHandler(_handlerConfig, rangeStructSize));

                    if (PlatformInfo.IsBionic)
                    {
                        config.StructAddressOffset = 16; // si_addr
                        config.StructWriteOffset = 8; // si_code
                    }

                    if (customSignalHandlerFactory != null)
                    {
                        _signalHandlerPtr = customSignalHandlerFactory(UnixSignalHandlerRegistration.GetSegfaultExceptionHandler().sa_handler, _signalHandlerPtr);
                    }

                    UnixSignalHandlerRegistration.SigAction old = UnixSignalHandlerRegistration.RegisterExceptionHandler(_signalHandlerPtr);

                    _unixOriginalAction = old;
                    config.UnixOldSigaction = (nuint)(ulong)old.sa_handler;
                    config.UnixOldSigaction3Arg = old.sa_flags & 4;
                }
                else
                {
                    config.StructAddressOffset = 40; // ExceptionInformation1
                    config.StructWriteOffset = 32; // ExceptionInformation0

                    _signalHandlerPtr = MapCode(NativeSignalHandlerGenerator.GenerateWindowsSignalHandler(_handlerConfig, rangeStructSize));

                    if (customSignalHandlerFactory != null)
                    {
                        _signalHandlerPtr = customSignalHandlerFactory(nint.Zero, _signalHandlerPtr);
                    }

                    WindowsSignalHandlerRegistration.RegisterExceptionHandler(_signalHandlerPtr);
                }

                _initialized = true;
            }
        }

        public static void InstallUnixAlternateStackForCurrentThread(nint stackPtr, ulong stackSize)
        {
            UnixSignalHandlerRegistration.RegisterAlternateStack(stackPtr, stackSize);
        }

        public static void UninstallUnixAlternateStackForCurrentThread()
        {
            UnixSignalHandlerRegistration.UnregisterAlternateStack();
        }

        public static void InstallUnixSignalHandler(int sigNum, nint action)
        {
            UnixSignalHandlerRegistration.RegisterExceptionHandler(sigNum, action);
        }

        /// <summary>
        /// Installs a wrapper around the already initialized Unix fault handler.
        /// This also works when another CPU backend initialized the shared handler first.
        /// </summary>
        public static void InstallUnixSignalWrapper(Func<nint, nint, nint> customSignalHandlerFactory)
            => InstallUnixSignalWrapper(customSignalHandlerFactory, null);

        internal static void InstallUnixSignalWrapper(Func<nint, nint, nint> customSignalHandlerFactory,
            Func<UnixSignalHandlerRegistration.SigAction, nint> previousActionFactory)
        {
            ArgumentNullException.ThrowIfNull(customSignalHandlerFactory);

            InitializeSignalHandler();

            lock (_lock)
            {
                if (_customUnixWrapperInstalled)
                {
                    return;
                }

                ref SignalHandlerConfig config = ref GetConfigRef();
                nuint previousPointer = config.UnixOldSigaction;
                int previousKind = config.UnixOldSigaction3Arg;
                bool androidChain = PlatformInfo.IsBionic && previousActionFactory != null;
                nint previous = androidChain ? previousActionFactory(_unixOriginalAction) : (nint)previousPointer;
                nint wrapper = customSignalHandlerFactory(previous, _signalHandlerPtr);

                if (androidChain)
                {
                    config.UnixOldSigaction = (nuint)previous;
                    config.UnixOldSigaction3Arg = 4; // Generated dispatcher always takes the full signal frame.
                }
                try
                {
                    UnixSignalHandlerRegistration.RegisterExceptionHandler(wrapper, blockNceSuspend: androidChain,
                        inheritedFlags: androidChain ? _unixOriginalAction.sa_flags : 0);
                }
                catch
                {
                    config.UnixOldSigaction = previousPointer;
                    config.UnixOldSigaction3Arg = previousKind;
                    throw;
                }
                _signalHandlerPtr = wrapper;
                _customUnixWrapperInstalled = true;
            }
        }

        private static nint MapCode(ReadOnlySpan<byte> code)
        {
            Debug.Assert(_codeBlock == null);

            ulong codeSizeAligned = BitUtils.AlignUp((ulong)code.Length, MemoryBlock.GetPageSize());

            _codeBlock = new MemoryBlock(codeSizeAligned);
            _codeBlock.Write(0, code);
            _codeBlock.Reprotect(0, codeSizeAligned, MemoryPermission.ReadAndExecute);

            return _codeBlock.Pointer;
        }

        private static unsafe ref SignalHandlerConfig GetConfigRef()
        {
            return ref Unsafe.AsRef<SignalHandlerConfig>((void*)_handlerConfig);
        }

        public static bool AddTrackedRegion(nuint address, nuint endAddress, nint action)
        {
            Span<SignalHandlerRange> ranges = GetConfigRef().Ranges;

            for (int i = 0; i < NativeSignalHandlerGenerator.MaxTrackedRanges; i++)
            {
                if (ranges[i].IsActive == 0)
                {
                    ranges[i].RangeAddress = address;
                    ranges[i].RangeEndAddress = endAddress;
                    ranges[i].ActionPointer = action;
                    ranges[i].IsActive = 1;

                    return true;
                }
            }

            return false;
        }

        public static bool RemoveTrackedRegion(nuint address)
        {
            Span<SignalHandlerRange> ranges = GetConfigRef().Ranges;

            for (int i = 0; i < NativeSignalHandlerGenerator.MaxTrackedRanges; i++)
            {
                if (ranges[i].IsActive == 1 && ranges[i].RangeAddress == address)
                {
                    ranges[i].IsActive = 0;

                    return true;
                }
            }

            return false;
        }

        public static bool SupportsFaultAddressPatching()
        {
            return NativeSignalHandlerGenerator.SupportsFaultAddressPatchingForHost();
        }
    }
}
