using LibKenjinx.Jni;
using LibKenjinx.Jni.Identifiers;
using LibKenjinx.Jni.Pointers;
using LibKenjinx.Jni.Primitives;
using LibKenjinx.Jni.References;
using LibKenjinx.Jni.Values;
using Rxmxnx.PInvoke;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace LibKenjinx.Android
{
    internal unsafe static class Interop
    {
        internal const string BaseClassName = "org/kenjinx/android/KenjinxNative";

        private static JGlobalRef? _classId;
        private static ConcurrentDictionary<(string method, string descriptor), JMethodId> _methodCache = new();
        private static (string name, string descriptor)[] _methods =
        [
            ("test", "()V"),
            ("updateUiHandler", "(JJJIIIIJJ)V"),
            ("frameEnded", "()V"),
            ("updateProgress", "(JF)V"),
            ("getSurfacePtr", "()J"),
            ("getWindowHandle", "()J"),
            ("onHostLowMemory", "()V")
        ];

        internal static void Initialize(JEnvRef jniEnv)
        {
            JavaVMRef? vm = JniHelper.GetVirtualMachine(jniEnv);
            if (_classId == null)
            {
                ReadOnlySpan<byte> className = new(Encoding.UTF8.GetBytes(BaseClassName));
                using (IReadOnlyFixedMemory<Byte>.IDisposable cName = className.GetUnsafeValPtr()
                           .GetUnsafeFixedContext(className.Length))
                {
                    _classId = JniHelper.GetGlobalClass(jniEnv, cName);
                    if (_classId == null)
                    {
                        Logger.Info?.Print(LogClass.Application, $"Class Id {BaseClassName} not found");
                        return;
                    }
                }
            }

            foreach ((string name, string descriptor) x in _methods)
            {
                CacheMethod(jniEnv, x.name, x.descriptor);
            }

            JniEnv._jvm = vm;
        }

        private static void CacheMethod(JEnvRef jEnv, string name, string descriptor)
        {
            if (!_methodCache.TryGetValue((name, descriptor), out JMethodId method))
            {
                ReadOnlySpan<byte> methodName = new(Encoding.UTF8.GetBytes(name));
                ReadOnlySpan<byte> descriptorId = new(Encoding.UTF8.GetBytes(descriptor));
                using (IReadOnlyFixedMemory<Byte>.IDisposable mName = methodName.GetUnsafeValPtr()
                       .GetUnsafeFixedContext(methodName.Length))
                using (IReadOnlyFixedMemory<Byte>.IDisposable dName = descriptorId.GetUnsafeValPtr()
                       .GetUnsafeFixedContext(descriptorId.Length))
                {
                    if (_classId != null)
                    {
                        JMethodId? methodId = JniHelper.GetStaticMethodId(jEnv, (JClassLocalRef)(_classId.Value.Value), mName, dName);
                        if (methodId == null)
                        {
                            Logger.Warning?.Print(LogClass.Application, $"Java Method Id {name} not found");
                            return;
                        }

                        method = methodId.Value;
                    }

                    _methodCache[(name, descriptor)] = method;
                }
            }
        }

        private static void CallVoidMethod(string name, string descriptor, params JValue[] values)
        {
            using JniEnv? env = JniEnv.Create();
            if (_methodCache.TryGetValue((name, descriptor), out JMethodId method))
            {
                if (descriptor.EndsWith("V"))
                {
                    if (env != null && _classId != null)
                    {
                        JniHelper.CallStaticVoidMethod(env.Env, (JClassLocalRef)(_classId.Value.Value), method, values);
                    }
                }
            }
        }

        private static JLong CallLongMethod(string name, string descriptor, params JValue[] values)
        {
            using JniEnv? env = JniEnv.Create();
            if (_methodCache.TryGetValue((name, descriptor), out JMethodId method))
            {
                if (descriptor.EndsWith("J"))
                    if (env != null && _classId != null)
                    {
                        return JniHelper.CallStaticLongMethod(env.Env, (JClassLocalRef)(_classId.Value.Value), method,
                            values) ?? -1;
                    }
            }

            return -1;
        }

        public static void Test()
        {
            CallVoidMethod("test", "()V");
        }

        // NEOV2-GPUMEM1: asks the app to leave the game (normal exit path) when the Android RAM is critical.
        public static void OnHostLowMemory()
        {
            CallVoidMethod("onHostLowMemory", "()V");
        }

        public static void FrameEnded(double time)
        {
            CallVoidMethod("frameEnded", "()V");
        }

        public static void UpdateProgress(string info, float progress)
        {
            using TempNativeString infoPtr = new(info);
            CallVoidMethod("updateProgress", "(JF)V",
                JValue.Create(infoPtr.AsBytes()),
                JValue.Create(progress.AsBytes()));
        }

        public static JLong GetSurfacePtr()
        {
            return CallLongMethod("getSurfacePtr", "()J");
        }

        public static JLong GetWindowsHandle()
        {
            return CallLongMethod("getWindowHandle", "()J");
        }

        public static void UpdateUiHandler(string newTitle,
            string newMessage,
            string newWatermark,
            int newType,
            int min,
            int max,
            KeyboardMode nMode,
            string newSubtitle,
            string newInitialText)
        {
            using TempNativeString titlePointer = new(newTitle);
            using TempNativeString messagePointer = new(newMessage);
            using TempNativeString watermarkPointer = new(newWatermark);
            using TempNativeString subtitlePointer = new(newSubtitle);
            using TempNativeString newInitialPointer = new(newInitialText);
            CallVoidMethod("updateUiHandler", "(JJJIIIIJJ)V",
                JValue.Create(titlePointer.AsBytes()),
                JValue.Create(messagePointer.AsBytes()),
                JValue.Create(watermarkPointer.AsBytes()),
                JValue.Create(newType.AsBytes()),
                JValue.Create(min.AsBytes()),
                JValue.Create(max.AsBytes()),
                JValue.Create(nMode.AsBytes()),
                JValue.Create(subtitlePointer.AsBytes()),
                JValue.Create(newInitialPointer.AsBytes()));
        }

        private class TempNativeString : IDisposable
        {
            private JLong _jPointer;

            public TempNativeString(string value)
            {
                Pointer = Marshal.StringToHGlobalAuto(value);
                JPointer = Pointer;
            }

            public nint Pointer { get; private set; }
            public JLong JPointer { get => _jPointer; private set => _jPointer = value; }

            public Span<byte> AsBytes()
            {
                return _jPointer.AsBytes();
            }

            public void Dispose()
            {
                if (Pointer != nint.Zero)
                {
                    Marshal.FreeHGlobal(Pointer);
                }
                Pointer = nint.Zero;
            }
        }

        private class JniEnv : IDisposable
        {
            internal static JavaVMRef? _jvm;
            private readonly JEnvRef _env;
            private readonly bool _newAttach;

            public JEnvRef Env => _env;

            private JniEnv(JEnvRef env, bool newAttach)
            {
                _env = env;
                _newAttach = newAttach;
            }

            public void Dispose()
            {
                if(_newAttach)
                {
                    JniHelper.Detach(_jvm!.Value);
                }
            }

            public static JniEnv? Create()
            {
                bool newAttach = false;
                ReadOnlySpan<Byte> threadName = "JvmCall"u8;
                JEnvRef? env = _jvm == null ? default : JniHelper.Attach(_jvm.Value, threadName.GetUnsafeValPtr().GetUnsafeFixedContext(threadName.Length),
                                         out newAttach);

                return env != null ? new JniEnv(env.Value, newAttach) : null;
            }
        }
    }
}
