using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    static class NceNativeInterface
    {
        private delegate ulong GetTickCounterDelegate();
        private delegate bool SuspendThreadHandlerDelegate();

        private static GetTickCounterDelegate _getTickCounter;
        private static SuspendThreadHandlerDelegate _suspendThreadHandler;

        private static nint _getTickCounterPtr;
        private static nint _suspendThreadHandlerPtr;

        [ThreadStatic]
        private static NceExecutionContext _context;

        [ThreadStatic]
        private static ITickSource _tickSource;

        static NceNativeInterface()
        {
            _getTickCounter = GetTickCounter;
            _suspendThreadHandler = SuspendThreadHandler;

            _getTickCounterPtr = Marshal.GetFunctionPointerForDelegate(_getTickCounter);
            _suspendThreadHandlerPtr = Marshal.GetFunctionPointerForDelegate(_suspendThreadHandler);
        }

        public static void RegisterThread(NceExecutionContext context, ITickSource tickSource)
        {
            _context = context;
            _tickSource = tickSource;
        }

        public static void UnregisterThread()
        {
            _context = null;
            _tickSource = null;
        }

        public static ulong GetTickCounter()
        {
            return _tickSource?.Counter ?? 0UL;
        }

        public static bool SuspendThreadHandler()
        {
            return _context?.OnInterrupt() ?? false;
        }

        public static nint GetTickCounterAccessFunctionPointer()
        {
            return _getTickCounterPtr;
        }

        public static nint GetSuspendThreadHandlerFunctionPointer()
        {
            return _suspendThreadHandlerPtr;
        }
    }
}
