using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    static class NceThreadPalUnix
    {
        [DllImport("libc", SetLastError = true)]
        private static extern nint pthread_self();

        [DllImport("libc", SetLastError = true)]
        private static extern int pthread_threadid_np(nint arg0, out ulong tid);

        [DllImport("libpthread.so.0", SetLastError = true)]
        private static extern int pthread_kill(nint thread, int sig);

        public static nint GetCurrentThreadHandle()
        {
            return pthread_self();
        }

        public static ulong GetCurrentThreadId()
        {
            pthread_threadid_np(nint.Zero, out ulong tid);
            return tid;
        }

        public static void SuspendThread(nint handle)
        {
            int result = pthread_kill(handle, NceThreadPal.UnixSuspendSignal);
            if (result != 0)
            {
                throw new Exception($"Thread kill returned error 0x{result:X}.");
            }
        }
    }
}
