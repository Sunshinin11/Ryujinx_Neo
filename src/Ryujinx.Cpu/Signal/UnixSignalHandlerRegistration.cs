using Ryujinx.Common;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.Signal
{
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    static partial class UnixSignalHandlerRegistration
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct SigSet
        {
            fixed long sa_mask[16];
        }

        // glibc AArch64 aligns sa_restorer to 8 bytes. Pack=1 made the managed
        // structure 148 bytes long, while libc reads/writes 152 bytes and keeps
        // sa_restorer at offset 144.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct SigAction
        {
            public nint sa_handler;
            public SigSet sa_mask;
            public int sa_flags;
            public nint sa_restorer;
        }

        [SupportedOSPlatform("android"), StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct SigActionBionic
        {
            public int sa_flags;
            public nint sa_handler;
            public SigSet sa_mask;
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
        private const int SA_SIGINFO = 0x00000004;
        private const int SA_ONSTACK = 0x08000000;
        private const int SS_DISABLE = 2;
        private const int SS_AUTODISARM = 1 << 31;

        private static readonly object _bionicSigactionLock = new();
        private static nint _bionicLibcHandle;
        private static nint _bionicSigaction;

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

        [LibraryImport("libc", SetLastError = true)]
        private static partial int sigaltstack(ref Stack ss, out Stack oldSs);

        private static unsafe int BionicSigaction(int signum, SigActionBionic* action, SigActionBionic* oldAction)
        {
            if (_bionicSigaction == nint.Zero)
            {
                lock (_bionicSigactionLock)
                {
                    if (_bionicSigaction == nint.Zero)
                    {
                        _bionicLibcHandle = NativeLibrary.Load("libc.so");
                        _bionicSigaction = NativeLibrary.GetExport(_bionicLibcHandle, "sigaction");
                    }
                }
            }

            // Resolve sigaction from libc itself on Android. Calling the public symbol through
            // the process can be intercepted by libsigchain, which adds host TLS work to every
            // guest protection fault and was a measurable NCE hot-path cost in Strato.
            return ((delegate* unmanaged<int, SigActionBionic*, SigActionBionic*, int>)_bionicSigaction)(signum, action, oldAction);
        }

        public static SigAction GetSegfaultExceptionHandler()
        {
            int result;
            SigAction old;

            if (PlatformInfo.IsBionic)
            {
                SigActionBionic tmp;
                unsafe
                {
                    result = BionicSigaction(SIGSEGV, null, &tmp);
                }

                old = new SigAction
                {
                    sa_handler = tmp.sa_handler,
                    sa_mask = tmp.sa_mask,
                    sa_flags = tmp.sa_flags,
                    sa_restorer = tmp.sa_restorer
                };
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

        public static SigAction RegisterExceptionHandler(nint action)
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

                SigActionBionic tmp;
                unsafe
                {
                    result = BionicSigaction(SIGSEGV, &sig, &tmp);
                }

                old = new SigAction
                {
                    sa_handler = tmp.sa_handler,
                    sa_mask = tmp.sa_mask,
                    sa_flags = tmp.sa_flags,
                    sa_restorer = tmp.sa_restorer
                };
            }
            else
            {
                SigAction sig = new()
                {
                    sa_handler = action,
                    // NCE faults can arrive while the guest SP is unsuitable
                    // for a host signal frame. Linux guest threads install an
                    // alternate stack specifically for this purpose.
                    sa_flags = SA_SIGINFO | (OperatingSystem.IsLinux() ? SA_ONSTACK : 0),
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

                SigActionBionic oldu;
                unsafe
                {
                    result = BionicSigaction(sigNum, &sig, &oldu);
                }

                if (oldu.sa_handler != nint.Zero)
                {
                    throw new InvalidOperationException($"SIG{sigNum} is already in use.");
                }

                if (result != 0)
                {
                    throw new SystemException($"Could not register SIG{sigNum} sigaction. Error: {Marshal.GetLastPInvokeErrorMessage()}");
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
                SigActionBionic tmp = new()
                {
                    sa_handler = oldAction.sa_handler,
                    sa_mask = oldAction.sa_mask,
                    sa_flags = oldAction.sa_flags,
                    sa_restorer = oldAction.sa_restorer
                };

                SigActionBionic discarded;
                unsafe
                {
                    return BionicSigaction(SIGSEGV, &tmp, &discarded) == 0;
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
