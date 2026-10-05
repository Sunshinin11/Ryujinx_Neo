using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.Nce;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.Signal
{
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    static partial class UnixSignalHandlerRegistration
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct SigSet
        {
            fixed long sa_mask[16];

            // Only the first 64 signal bits belong to the Android LP64 ABI.
            public ulong GetBionicMask()
            {
                fixed (long* mask = sa_mask)
                {
                    return unchecked((ulong)mask[0]);
                }
            }

            public static SigSet FromBionicMask(ulong mask)
            {
                SigSet result = default;
                result.sa_mask[0] = unchecked((long)mask);
                return result;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SigAction
        {
            public nint sa_handler;
            public SigSet sa_mask;
            public int sa_flags;
            public nint sa_restorer;
        }

        // Bionic LP64 sigset_t is one unsigned long, not glibc's 128-byte mask.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct SigSetBionic
        {
            public ulong Mask;
        }

        [SupportedOSPlatform("android"), StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct SigActionBionic
        {
            public int sa_flags;
            public nint sa_handler;
            public SigSetBionic sa_mask;
            public nint sa_restorer;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct Stack
        {
            public nint ss_sp;
            public int ss_flags;
            public nint ss_size;
        }

        private const int SIGSEGV = 11;
        private const int SIGBUS = 10;
        internal static ulong NceSuspendMask => 1UL << (NceThreadPal.UnixSuspendSignal - 1);

        private const int SA_SIGINFO = 0x00000004;
        private const int SA_ONSTACK = 0x08000000;
        private const int SS_DISABLE = 2;
        private const int SS_AUTODISARM = 1 << 31;

        private static readonly object _bionicSigactionLock = new();
        private static nint _bionicLibcHandle;
        private static nint _bionicSigaction;
        private static nint _bionicErrno;
        private const string BionicRound = "nce-r2-bionic-abi-20261002";

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, ref SigAction sigAction, out SigAction oldAction);

        [SupportedOSPlatform("android"), LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, ref SigActionBionic sigAction, out SigActionBionic oldAction);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, nint sigAction, out SigAction oldAction);

        [SupportedOSPlatform("android"), LibraryImport("libc", SetLastError = true)]
        private static partial int sigaction(int signum, nint sigAction, out SigActionBionic oldAction);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigemptyset(ref SigSet set);

        [SupportedOSPlatform("android"), LibraryImport("libc", SetLastError = true)]
        private static partial int sigemptyset(ref SigSetBionic set);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigaltstack(ref Stack ss, out Stack oldSs);

        private static void ValidateBionicLayout()
        {
            if (nint.Size != 8 || Marshal.SizeOf<SigSetBionic>() != 8 ||
                Marshal.SizeOf<SigActionBionic>() != 32 ||
                Marshal.OffsetOf<SigActionBionic>(nameof(SigActionBionic.sa_flags)) != 0 ||
                Marshal.OffsetOf<SigActionBionic>(nameof(SigActionBionic.sa_handler)) != 8 ||
                Marshal.OffsetOf<SigActionBionic>(nameof(SigActionBionic.sa_mask)) != 16 ||
                Marshal.OffsetOf<SigActionBionic>(nameof(SigActionBionic.sa_restorer)) != 24)
            {
                throw new PlatformNotSupportedException("Bionic sigaction requires the verified Android LP64 layout.");
            }
        }

        private static SigAction ToCommonSigAction(SigActionBionic action) => new()
        {
            sa_handler = action.sa_handler,
            sa_mask = SigSet.FromBionicMask(action.sa_mask.Mask),
            sa_flags = action.sa_flags,
            sa_restorer = action.sa_restorer
        };

        private static SigActionBionic FromCommonSigAction(SigAction action) => new()
        {
            sa_handler = action.sa_handler,
            sa_mask = new SigSetBionic { Mask = action.sa_mask.GetBionicMask() },
            sa_flags = action.sa_flags,
            sa_restorer = action.sa_restorer
        };

        private static unsafe int BionicSigaction(int signum, SigActionBionic* action, SigActionBionic* oldAction, string operation)
        {
            nint entry = Volatile.Read(ref _bionicSigaction);
            if (entry == nint.Zero)
            {
                lock (_bionicSigactionLock)
                {
                    entry = _bionicSigaction;
                    if (entry == nint.Zero)
                    {
                        ValidateBionicLayout();
                        if (_bionicLibcHandle == nint.Zero)
                        {
                            _bionicLibcHandle = NativeLibrary.Load("libc.so");
                        }
                        _bionicErrno = NativeLibrary.GetExport(_bionicLibcHandle, "__errno");
                        entry = NativeLibrary.GetExport(_bionicLibcHandle, "sigaction");
                        // Publish both exports together; this is registration, never a guest fault.
                        Volatile.Write(ref _bionicSigaction, entry);
                    }
                }
            }

            // Keep direct libc resolution: libsigchain interception adds host TLS work
            // to guest protection faults. This wrapper only runs at query/install/restore.
            int result = ((delegate* unmanaged<int, SigActionBionic*, SigActionBionic*, int>)entry)(signum, action, oldAction);
            // A raw unmanaged function pointer does not populate the P/Invoke error cache.
            int error = result == 0 ? 0 : *((delegate* unmanaged<int*>)_bionicErrno)();


            // Set after logging, so diagnostics cannot replace the error seen by callers.
            Marshal.SetLastPInvokeError(error);
            return result;
        }

        public static SigAction GetSegfaultExceptionHandler()
        {
            int result;
            SigAction old;

            if (PlatformInfo.IsBionic)
            {
                SigActionBionic tmp = default;
                unsafe
                {
                    result = BionicSigaction(SIGSEGV, null, &tmp, "query");
                }

                if (result != 0)
                {
                    throw new SystemException($"Could not get SIGSEGV sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                }

                old = ToCommonSigAction(tmp);
            }
            else
            {
                result = sigaction(SIGSEGV, nint.Zero, out old);
            }

            if (result != 0)
            {
                throw new SystemException($"Could not get SIGSEGV sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
            }

            return old;
        }

        public static SigAction RegisterExceptionHandler(nint action, bool blockNceSuspend = false, int inheritedFlags = 0)
        {
            int result;
            SigAction old;

            if (PlatformInfo.IsBionic)
            {
                SigActionBionic sig = new()
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO | SA_ONSTACK,
                };

                sigemptyset(ref sig.sa_mask);
                // Keep SIGUSR2 blocked until kernel signal-return restores the interrupted frame.
                // Only NCE Bionic wrappers request this; no per-fault mask syscall.
                if (blockNceSuspend) sig.sa_mask.Mask |= NceSuspendMask;
                // Preserve kernel restart/tag exposure requested by the original action.
                // NODEFER/RESETHAND are handled by the prior-action dispatcher, not by disabling tracking.
                if (blockNceSuspend) sig.sa_flags |= inheritedFlags & (0x10000000 | 0x00000800);

                SigActionBionic tmp = default;
                unsafe
                {
                    result = BionicSigaction(SIGSEGV, &sig, &tmp, "install");
                }

                if (result != 0)
                {
                    throw new SystemException($"Could not register SIGSEGV sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                }

                old = ToCommonSigAction(tmp);
            }
            else
            {
                SigAction sig = new()
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO,
                };

                sigemptyset(ref sig.sa_mask);

                result = sigaction(SIGSEGV, ref sig, out old);

                if (result != 0)
                {
                    throw new SystemException($"Could not register SIGSEGV sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                }

                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    result = sigaction(SIGBUS, ref sig, out _);

                    if (result != 0)
                    {
                        throw new SystemException($"Could not register SIGBUS sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                    }
                }
            }

            return old;
        }

        public static void RegisterAlternateStack(nint stackPtr, ulong stackSize)
        {
            Stack stack = new()
            {
                ss_sp = stackPtr,
                ss_flags = SS_AUTODISARM,
                ss_size = (nint)stackSize
            };

            int result = sigaltstack(ref stack, out _);

            if (result != 0)
            {
                throw new SystemException($"Could not set alternate stack. Error: {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        public static void UnregisterAlternateStack()
        {
            Stack stack = new()
            {
                ss_flags = SS_DISABLE
            };

            int result = sigaltstack(ref stack, out _);

            if (result != 0)
            {
                throw new SystemException($"Could not remove alternate stack. Error: {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        public static void RegisterExceptionHandler(int sigNum, nint action)
        {
            int result;

            if (PlatformInfo.IsBionic)
            {
                SigActionBionic sig = new()
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO | SA_ONSTACK
                };

                sigemptyset(ref sig.sa_mask);

                SigActionBionic oldu = default;
                unsafe
                {
                    result = BionicSigaction(sigNum, &sig, &oldu, "install");
                }

                if (result != 0)
                {
                    throw new SystemException($"Could not register SIG{sigNum} sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                }

                if (oldu.sa_handler != nint.Zero)
                {
                    throw new InvalidOperationException($"SIG{sigNum} is already in use.");
                }
            }
            else
            {
                SigAction sig = new()
                {
                    sa_handler = action,
                    sa_flags = SA_SIGINFO | SA_ONSTACK,
                };

                sigemptyset(ref sig.sa_mask);

                result = sigaction(sigNum, ref sig, out SigAction oldu);

                if (oldu.sa_handler != nint.Zero)
                {
                    throw new InvalidOperationException($"SIG{sigNum} is already in use.");
                }

                if (result != 0)
                {
                    throw new SystemException($"Could not register SIG{sigNum} sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
                }
            }
        }

        public static bool RestoreExceptionHandler(SigAction oldAction)
        {
            if (PlatformInfo.IsBionic)
            {
                SigActionBionic tmp = FromCommonSigAction(oldAction);

                SigActionBionic discarded = default;
                unsafe
                {
                    return BionicSigaction(SIGSEGV, &tmp, &discarded, "restore") == 0;
                }
            }
            else
            {
                bool success = sigaction(SIGSEGV, ref oldAction, out SigAction _) == 0;

                if (success && (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()))
                {
                    success = sigaction(SIGBUS, ref oldAction, out SigAction _) == 0;
                }

                return success;
            }
        }
    }
}
