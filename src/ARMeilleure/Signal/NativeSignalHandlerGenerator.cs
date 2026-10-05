using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using System;
using System.Runtime.InteropServices;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Signal
{
    public static class NativeSignalHandlerGenerator
    {
        public const int MaxTrackedRanges = 8;

        private const int StructAddressOffset = 0;
        private const int StructWriteOffset = 4;
        private const int UnixOldSigaction = 8;
        private const int UnixOldSigaction3Arg = 16;
        private const int RangeOffset = 20;

        private const int EXCEPTION_CONTINUE_SEARCH = 0;
        private const int EXCEPTION_CONTINUE_EXECUTION = -1;

        private const uint EXCEPTION_ACCESS_VIOLATION = 0xc0000005;

        private static Operand EmitGenericRegionCheck(EmitterContext context, nint signalStructPtr, Operand faultAddress, Operand isWrite, int rangeStructSize, bool androidFrame = false)
        {
            Operand inRegionLocal = context.AllocateLocal(OperandType.I32);
            context.Copy(inRegionLocal, Const(0));

            Operand endLabel = Label();

            for (int i = 0; i < MaxTrackedRanges; i++)
            {
                ulong rangeBaseOffset = (ulong)(RangeOffset + i * rangeStructSize);

                Operand nextLabel = Label();

                Operand isActive = context.Load(OperandType.I32, Const((ulong)signalStructPtr + rangeBaseOffset));

                context.BranchIfFalse(nextLabel, isActive);

                Operand rangeAddress = context.Load(OperandType.I64, Const((ulong)signalStructPtr + rangeBaseOffset + 4));
                Operand rangeEndAddress = context.Load(OperandType.I64, Const((ulong)signalStructPtr + rangeBaseOffset + 12));

                // Is the fault address within this tracked region?
                Operand inRange = context.BitwiseAnd(
                    context.ICompare(faultAddress, rangeAddress, Comparison.GreaterOrEqualUI),
                    context.ICompare(faultAddress, rangeEndAddress, Comparison.LessUI));

                // Only call tracking if in range.
                context.BranchIfFalse(nextLabel, inRange, BasicBlockFrequency.Cold);

                Operand offset = context.Subtract(faultAddress, rangeAddress);

                // Call the tracking action, with the pointer's relative offset to the base address.
                Operand trackingActionPtr = context.Load(OperandType.I64, Const((ulong)signalStructPtr + rangeBaseOffset + 20));

                context.Copy(inRegionLocal, Const(0));

                Operand skipActionLabel = Label();

                // Tracking action should be non-null to call it, otherwise assume false return.
                context.BranchIfFalse(skipActionLabel, trackingActionPtr);
                Operand result = context.Call(trackingActionPtr, OperandType.I64, offset, Const(1UL), isWrite);
                context.Copy(inRegionLocal, context.ICompareNotEqual(result, Const(0UL)));

                context.BranchIfFalse(skipActionLabel, result); // An unhandled fault must leave the signal frame intact.
                GenerateFaultAddressPatchCode(context, faultAddress, result, androidFrame);

                context.MarkLabel(skipActionLabel);

                // If the tracking action returns false or does not exist, it might be an invalid access due to a partial overlap on Windows.
                if (OperatingSystem.IsWindows() && !androidFrame)
                {
                    context.BranchIfTrue(endLabel, inRegionLocal);

                    context.Copy(inRegionLocal, WindowsPartialUnmapHandler.EmitRetryFromAccessViolation(context));
                }

                context.Branch(endLabel);

                context.MarkLabel(nextLabel);
            }

            context.MarkLabel(endLabel);

            return context.Copy(inRegionLocal);
        }

        private static Operand GenerateUnixFaultAddress(EmitterContext context, Operand sigInfoPtr)
        {
            ulong structAddressOffset = OperatingSystem.IsMacOS() ? 24ul : 16ul; // si_addr
            return context.Load(OperandType.I64, context.Add(sigInfoPtr, Const(structAddressOffset)));
        }

        private static Operand GenerateUnixWriteFlag(EmitterContext context, Operand ucontextPtr)
        {
            if (OperatingSystem.IsMacOS())
            {
                const ulong McontextOffset = 48; // uc_mcontext
                Operand ctxPtr = context.Load(OperandType.I64, context.Add(ucontextPtr, Const(McontextOffset)));

                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    const ulong EsrOffset = 8; // __es.__esr
                    Operand esr = context.Load(OperandType.I64, context.Add(ctxPtr, Const(EsrOffset)));
                    return context.BitwiseAnd(esr, Const(0x40ul));
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    const ulong ErrOffset = 4; // __es.__err
                    Operand err = context.Load(OperandType.I64, context.Add(ctxPtr, Const(ErrOffset)));
                    return context.BitwiseAnd(err, Const(2ul));
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    return GenerateLinuxArm64WriteFlag(context, ucontextPtr);
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    const int ErrOffset = 192; // uc_mcontext.gregs[REG_ERR]
                    Operand err = context.Load(OperandType.I64, context.Add(ucontextPtr, Const(ErrOffset)));
                    return context.BitwiseAnd(err, Const(2ul));
                }
            }

            throw new PlatformNotSupportedException();
        }

        // Linux/Android ARM64 places ESR only in sigcontext.__reserved, never extra_context.
        // Emit native IR: this routine is not called from the signal handler as managed code.
        internal static Operand GenerateLinuxArm64WriteFlag(EmitterContext context, Operand ucontextPtr)
            => GenerateLinuxArm64WriteFlag(context, ucontextPtr, false);

        internal static Operand GenerateLinuxArm64TrackingWriteFlag(EmitterContext context, Operand ucontextPtr)
            => GenerateLinuxArm64WriteFlag(context, ucontextPtr, true);

        private static Operand GenerateLinuxArm64WriteFlag(EmitterContext context, Operand ucontextPtr, bool requireDataAbort)
        {
            const ulong AuxOffset = 464; // uc_mcontext.__reserved, verified against Bionic LP64.
            const ulong ReservedSize = 4096;
            const uint EsrMagic = 0x45535201;
            const uint ExtraMagic = 0x45585401;

            Operand auxPtr = context.AllocateLocal(OperandType.I64);
            Operand remaining = context.AllocateLocal(OperandType.I64);
            Operand writeFlag = context.AllocateLocal(OperandType.I64);
            Operand loopLabel = Label();
            Operand esrLabel = Label();
            Operand endLabel = Label();

            // Unknown is distinct from read (0) and write (0x40). Do not guess a read.
            context.Copy(writeFlag, Const(-1L));
            context.BranchIfFalse(endLabel, ucontextPtr);
            context.Copy(auxPtr, context.Add(ucontextPtr, Const(AuxOffset)));
            context.BranchIf(endLabel, auxPtr, ucontextPtr, Comparison.LessUI);
            context.BranchIfTrue(endLabel, context.BitwiseAnd(auxPtr, Const(15UL)));
            Operand reservedEnd = context.Add(auxPtr, Const(ReservedSize));
            context.BranchIf(endLabel, reservedEnd, auxPtr, Comparison.LessUI);
            context.Copy(remaining, Const(ReservedSize));

            context.MarkLabel(loopLabel);
            // Positions and record sizes are 16-byte aligned; never load a header past the area.
            // Invariant: remaining is a nonnegative multiple of 16, so nonzero implies >= 16.
            context.BranchIfFalse(endLabel, remaining);
            Operand magic = context.Load(OperandType.I32, auxPtr);
            Operand size = context.Load(OperandType.I32, context.Add(auxPtr, Const(4UL)));
            // The normal ESR fast path needs only the exact size check below.
            context.BranchIf(esrLabel, magic, Const(EsrMagic), Comparison.Equal);
            // ESR cannot appear after a valid EXTRA record. Do not follow datap, even if forged.
            context.BranchIf(endLabel, magic, Const(ExtraMagic), Comparison.Equal);
            context.BranchIfFalse(endLabel, magic); // Null terminator or malformed null magic.
            context.BranchIfFalse(endLabel, size); // Prevent a zero-size loop.
            context.BranchIfTrue(endLabel, context.BitwiseAnd(size, Const(15)));
            Operand size64 = context.ZeroExtend32(OperandType.I64, size);
            context.BranchIf(endLabel, size64, remaining, Comparison.GreaterUI);
            context.Copy(auxPtr, context.Add(auxPtr, size64));
            context.Copy(remaining, context.Subtract(remaining, size64));
            context.Branch(loopLabel);

            context.MarkLabel(esrLabel);
            context.BranchIf(endLabel, size, Const(16), Comparison.NotEqual);
            Operand esr = context.Load(OperandType.I64, context.Add(auxPtr, Const(8UL)));
            if (requireDataAbort)
            {
                Operand ec = context.BitwiseAnd(esr, Const(0xfc000000UL));
                Operand dataAbort = context.BitwiseOr(context.ICompareEqual(ec, Const(0x90000000UL)),
                    context.ICompareEqual(ec, Const(0x94000000UL)));
                context.BranchIfFalse(endLabel, dataAbort);
            }
            context.Copy(writeFlag, context.BitwiseAnd(esr, Const(0x40UL)));

            context.MarkLabel(endLabel);
            return context.Copy(writeFlag);
        }

        private static bool IsAndroidArm64
        {
            get
            {
#if ANDROID
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
#else
                return OperatingSystem.IsAndroid() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
#endif
            }
        }

        public static byte[] GenerateUnixSignalHandler(nint signalStructPtr, int rangeStructSize)
            => GenerateUnixSignalHandlerCore(signalStructPtr, rangeStructSize,
                IsAndroidArm64,
                RuntimeInformation.ProcessArchitecture);

        // Same production Android IR; only the compiler target changes in host tests. No signals installed.
        internal static byte[] GenerateAndroidFaultDispatcherForTest(nint signalStructPtr, int rangeStructSize, Architecture target)
            => GenerateUnixSignalHandlerCore(signalStructPtr, rangeStructSize, true, target);

        private static byte[] GenerateUnixSignalHandlerCore(nint signalStructPtr, int rangeStructSize, bool androidFrame, Architecture target)
        {
            EmitterContext context = new();

            // (int sig, SigInfo* sigInfo, void* ucontext)
            Operand sigInfoPtr = context.LoadArgument(OperandType.I64, 1);
            Operand ucontextPtr = context.LoadArgument(OperandType.I64, 2);

            bool boundedArm64Esr = androidFrame || (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64);
            Operand chainLabel = boundedArm64Esr ? Label() : default;
            if (androidFrame)
            {
                context.BranchIfFalse(chainLabel, sigInfoPtr);
                context.BranchIfFalse(chainLabel, ucontextPtr);
                context.BranchIf(chainLabel, context.LoadArgument(OperandType.I32, 0), Const(11), Comparison.NotEqual);
                Operand code = context.Load(OperandType.I32, context.Add(sigInfoPtr, Const(8UL)));
                Operand memoryFault = context.BitwiseOr(context.ICompareEqual(code, Const(1)), context.ICompareEqual(code, Const(2)));
                context.BranchIfFalse(chainLabel, memoryFault); // Software signals, instruction aborts and MTE are not dirty-page faults.
            }
            Operand faultAddress = androidFrame ? context.Load(OperandType.I64, context.Add(sigInfoPtr, Const(16UL))) : GenerateUnixFaultAddress(context, sigInfoPtr);
            // TBI tags may be exposed by the inherited action. Match the actual VA reservation,
            // retaining original siginfo/register tags for the previous handler and kernel return.
            if (androidFrame) faultAddress = context.BitwiseAnd(faultAddress, Const(0x00ffffffffffffffUL));
            Operand writeFlag = androidFrame ? GenerateLinuxArm64TrackingWriteFlag(context, ucontextPtr) : GenerateUnixWriteFlag(context, ucontextPtr);
            if (boundedArm64Esr)
            {
                // Missing/malformed ESR must bypass tracking and use the existing handler chain.
                context.BranchIf(chainLabel, writeFlag, Const(-1L), Comparison.Equal);
            }

            Operand isWrite = context.ICompareNotEqual(writeFlag, Const(0L)); // Normalize to 0/1.

            Operand isInRegion = EmitGenericRegionCheck(context, signalStructPtr, faultAddress, isWrite, rangeStructSize, androidFrame);

            Operand endLabel = Label();

            context.BranchIfTrue(endLabel, isInRegion);

            if (boundedArm64Esr)
            {
                context.MarkLabel(chainLabel);
            }

            Operand unixOldSigaction = context.Load(OperandType.I64, Const((ulong)signalStructPtr + UnixOldSigaction));
            Operand unixOldSigaction3Arg = context.Load(OperandType.I32, Const((ulong)signalStructPtr + UnixOldSigaction3Arg));
            Operand threeArgLabel = Label();

            context.BranchIfTrue(threeArgLabel, unixOldSigaction3Arg);

            context.Call(unixOldSigaction, OperandType.None, context.LoadArgument(OperandType.I32, 0));
            context.Branch(endLabel);

            context.MarkLabel(threeArgLabel);

            context.Call(unixOldSigaction,
                OperandType.None,
                context.LoadArgument(OperandType.I32, 0),
                sigInfoPtr,
                context.LoadArgument(OperandType.I64, 2)
                );

            context.MarkLabel(endLabel);

            context.Return();

            ControlFlowGraph cfg = context.GetControlFlowGraph();

            OperandType[] argTypes = [OperandType.I32, OperandType.I64, OperandType.I64];

            return Compiler.Compile(cfg, argTypes, OperandType.None, CompilerOptions.HighCq, target).Code;
        }

        public static byte[] GenerateWindowsSignalHandler(nint signalStructPtr, int rangeStructSize)
        {
            EmitterContext context = new();

            // (ExceptionPointers* exceptionInfo)
            Operand exceptionInfoPtr = context.LoadArgument(OperandType.I64, 0);
            Operand exceptionRecordPtr = context.Load(OperandType.I64, exceptionInfoPtr);

            // First thing's first - this catches a number of exceptions, but we only want access violations.
            Operand validExceptionLabel = Label();

            Operand exceptionCode = context.Load(OperandType.I32, exceptionRecordPtr);

            context.BranchIf(validExceptionLabel, exceptionCode, Const(EXCEPTION_ACCESS_VIOLATION), Comparison.Equal);

            context.Return(Const(EXCEPTION_CONTINUE_SEARCH)); // Don't handle this one.

            context.MarkLabel(validExceptionLabel);

            // Next, read the address of the invalid access, and whether it is a write or not.

            Operand structAddressOffset = context.Load(OperandType.I32, Const((ulong)signalStructPtr + StructAddressOffset));
            Operand structWriteOffset = context.Load(OperandType.I32, Const((ulong)signalStructPtr + StructWriteOffset));

            Operand faultAddress = context.Load(OperandType.I64, context.Add(exceptionRecordPtr, context.ZeroExtend32(OperandType.I64, structAddressOffset)));
            Operand writeFlag = context.Load(OperandType.I64, context.Add(exceptionRecordPtr, context.ZeroExtend32(OperandType.I64, structWriteOffset)));

            Operand isWrite = context.ICompareNotEqual(writeFlag, Const(0L)); // Normalize to 0/1.

            Operand isInRegion = EmitGenericRegionCheck(context, signalStructPtr, faultAddress, isWrite, rangeStructSize);

            Operand endLabel = Label();

            // If the region check result is false, then run the next vectored exception handler.

            context.BranchIfTrue(endLabel, isInRegion);

            context.Return(Const(EXCEPTION_CONTINUE_SEARCH));

            context.MarkLabel(endLabel);

            // Otherwise, return to execution.

            context.Return(Const(EXCEPTION_CONTINUE_EXECUTION));

            // Compile and return the function.

            ControlFlowGraph cfg = context.GetControlFlowGraph();

            OperandType[] argTypes = [OperandType.I64];

            return Compiler.Compile(cfg, argTypes, OperandType.I32, CompilerOptions.HighCq, RuntimeInformation.ProcessArchitecture).Code;
        }

        private static void GenerateFaultAddressPatchCode(EmitterContext context, Operand faultAddress, Operand newAddress, bool androidFrame = false)
        {
            if (androidFrame || RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                if (androidFrame || SupportsFaultAddressPatchingForHostOs())
                {
                    Operand lblSkip = Label();

                    context.BranchIf(lblSkip, faultAddress, newAddress, Comparison.Equal);

                    Operand ucontextPtr = context.LoadArgument(OperandType.I64, 2);
                    Operand pcCtxAddress = default;
                    ulong baseRegsOffset = 0;

                    if (androidFrame || OperatingSystem.IsLinux())
                    {
                        pcCtxAddress = context.Add(ucontextPtr, Const(440UL));
                        baseRegsOffset = 184UL;
                    }
                    else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                    {
                        ucontextPtr = context.Load(OperandType.I64, context.Add(ucontextPtr, Const(48UL)));

                        pcCtxAddress = context.Add(ucontextPtr, Const(272UL));
                        baseRegsOffset = 16UL;
                    }

                    Operand pc = context.Load(OperandType.I64, pcCtxAddress);

                    Operand reg = GetAddressRegisterFromArm64Instruction(context, pc);
                    Operand reg64 = context.ZeroExtend32(OperandType.I64, reg);
                    Operand regCtxAddress = context.Add(ucontextPtr, context.Add(context.ShiftLeft(reg64, Const(3)), Const(baseRegsOffset)));
                    Operand regAddress = context.Load(OperandType.I64, regCtxAddress);

                    Operand addressDelta = context.Subtract(regAddress, faultAddress);

                    context.Store(regCtxAddress, context.Add(newAddress, addressDelta));

                    context.MarkLabel(lblSkip);
                }
            }
        }

        private static Operand GetAddressRegisterFromArm64Instruction(EmitterContext context, Operand pc)
        {
            Operand inst = context.Load(OperandType.I32, pc);
            Operand reg = context.AllocateLocal(OperandType.I32);

            Operand isSysInst = context.ICompareEqual(context.BitwiseAnd(inst, Const(0xFFF80000)), Const(0xD5080000));

            Operand lblSys = Label();
            Operand lblEnd = Label();

            context.BranchIfTrue(lblSys, isSysInst, BasicBlockFrequency.Cold);

            context.Copy(reg, context.BitwiseAnd(context.ShiftRightUI(inst, Const(5)), Const(0x1F)));
            context.Branch(lblEnd);

            context.MarkLabel(lblSys);
            context.Copy(reg, context.BitwiseAnd(inst, Const(0x1F)));

            context.MarkLabel(lblEnd);

            return reg;
        }

        public static bool SupportsFaultAddressPatchingForHost()
        {
            return SupportsFaultAddressPatchingForHostArch() && SupportsFaultAddressPatchingForHostOs();
        }

        private static bool SupportsFaultAddressPatchingForHostArch()
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        }

        private static bool SupportsFaultAddressPatchingForHostOs()
        {
            return OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
        }
    }
}
