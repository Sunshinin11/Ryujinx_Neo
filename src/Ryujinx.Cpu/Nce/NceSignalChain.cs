using Ryujinx.Cpu.Nce.Arm64;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    // Android AArch64 only. All handler work below is emitted native code: no managed
    // logger, allocator, resolver or lock is entered from a signal. Keep these blocks
    // alive for the process-wide handler lifetime, like NceCpuContext's helper block.
    internal static class NceSignalChain
    {
        private static MemoryBlock _code, _resetState;
        internal const int LinuxSigmaskOffset = 40;
        private const uint SigInfo = 4, NoDefer = 0x40000000, ResetHand = 0x80000000;
        private const ulong SegfaultMask = 1UL << 10;

        internal static nint Create(UnixSignalHandlerRegistration.SigAction previous)
        {
            _resetState = new MemoryBlock(0x1000);
            _resetState.Write(0, new byte[4]);
            uint[] words = Generate(previous.sa_handler, previous.sa_mask.GetBionicMask(),
                unchecked((uint)previous.sa_flags), _resetState.Pointer);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(words.AsSpan());
            _code = new MemoryBlock(((ulong)bytes.Length + 0xfffUL) & ~0xfffUL);
            _code.Write(0, bytes);
            _code.Reprotect(0, _code.Size, MemoryPermission.ReadAndExecute);
            return _code.Pointer;
        }

        internal static uint[] Generate(nint previousHandler, ulong previousMask, uint previousFlags, nint resetState)
        {
            Assembler asm = new();
            RegisterSaveRestore save = new((1 << 19) | (1 << 20) | (1 << 21) | (1 << 22), hasCall: true);
            Operand fatal = asm.CreateLabel(), exit = asm.CreateLabel(), done = asm.CreateLabel();
            Operand sp = R(31), zero = R(31);
            save.WritePrologue(asm);
            asm.Sub(sp, sp, C(64)); // mask/saved mask/kernel sigaction, private to this signal frame.
            asm.Mov(R(19), R(0));
            asm.Mov(R(20), R(1));
            asm.Mov(R(21), R(2));
            asm.Cmp(R(19), C(11)); // This chain is installed only for SIGSEGV.
            asm.B(exit, ArmCondition.Ne);

            if (previousHandler == nint.Zero)
                asm.B(fatal);
            else if (previousHandler == (nint)1)
                asm.B(done); // SIG_IGN is a disposition, never a code pointer.
            else
            {
                if ((previousFlags & ResetHand) != 0)
                {
                    if (resetState == nint.Zero) throw new ArgumentException("SA_RESETHAND needs live atomic state.", nameof(resetState));
                    Operand retry = asm.CreateLabel();
                    asm.Mov(R(22), (ulong)resetState);
                    asm.MarkLabel(retry);
                    asm.Ldaxr(R(9, OperandType.I32), R(22));
                    asm.Cbnz(R(9, OperandType.I32), fatal);
                    asm.Mov(R(9, OperandType.I32), 1UL);
                    asm.Stlxr(R(9, OperandType.I32), R(22), R(10, OperandType.I32));
                    asm.Cbnz(R(10, OperandType.I32), retry);
                }
                asm.Cbz(R(21), fatal);
                // The interrupted mask lives in ucontext, not in the wrapper's expanded mask.
                // Respect the previous handler's mask and Linux SA_NODEFER semantics.
                asm.LdrRiUn(R(9), R(21), LinuxSigmaskOffset);
                asm.Mov(R(10), previousMask | ((previousFlags & NoDefer) == 0 ? SegfaultMask : 0UL));
                asm.Orr(R(9), R(9), R(10));
                asm.StrRiUn(R(9), sp, 0);
                asm.Mov(R(0), 2UL); // SIG_SETMASK
                asm.MovSp(R(1), sp);
                asm.Add(R(2), sp, C(8)); // save the wrapper's current mask
                asm.Mov(R(3), 8UL);
                Syscall(asm, 135); // rt_sigprocmask
                asm.Cbnz(R(0), fatal);
                asm.Mov(R(0), R(19));
                if ((previousFlags & SigInfo) != 0)
                {
                    asm.Mov(R(1), R(20));
                    asm.Mov(R(2), R(21));
                }
                asm.Mov(R(22), (ulong)previousHandler);
                asm.Blr(R(22));
                asm.Mov(R(0), 2UL);
                asm.Add(R(1), sp, C(8));
                asm.Mov(R(2), zero);
                asm.Mov(R(3), 8UL);
                Syscall(asm, 135);
                asm.Cbnz(R(0), exit); // Never resume with an unknown mask after a failed restore.
                asm.B(done);
            }

            asm.MarkLabel(fatal);
            asm.Clrex();
            // The Linux syscall struct is handler/flags/restorer/mask, unlike Bionic libc's
            // flags/handler/mask/restorer. All-zero is a real SIG_DFL action in either layout.
            asm.StpRiUn(zero, zero, sp, 16);
            asm.StpRiUn(zero, zero, sp, 32);
            asm.Mov(R(0), R(19));
            asm.Add(R(1), sp, C(16));
            asm.Mov(R(2), zero);
            asm.Mov(R(3), 8UL);
            Syscall(asm, 134); // rt_sigaction(SIGSEGV, SIG_DFL)
            asm.Cbnz(R(0), exit);
            asm.Mov(R(9), SegfaultMask);
            asm.StrRiUn(R(9), sp, 0);
            asm.Mov(R(0), 1UL); // SIG_UNBLOCK
            asm.MovSp(R(1), sp);
            asm.Mov(R(2), zero);
            asm.Mov(R(3), 8UL);
            Syscall(asm, 135);
            asm.Cbnz(R(0), exit);
            Syscall(asm, 172); // getpid
            asm.Mov(R(22), R(0));
            Syscall(asm, 178); // gettid
            asm.Mov(R(1), R(0));
            asm.Mov(R(0), R(22));
            asm.Mov(R(2), R(19));
            Syscall(asm, 131); // tgkill current thread: normal fatal signal disposition/core dump.
            asm.MarkLabel(exit);
            asm.Add(R(0), R(19), C(128));
            Syscall(asm, 94); // exit_group fallback if delivery or a mask syscall failed.
            asm.B(exit); // No return to a faulting instruction and no jump to sentinel 0/1.

            asm.MarkLabel(done);
            asm.Add(sp, sp, C(64));
            save.WriteEpilogue(asm);
            asm.Ret(R(30));
            return asm.GetCode();
        }

        private static void Syscall(Assembler asm, int number)
        {
            asm.Mov(R(8), (ulong)number);
            asm.WriteInstruction(0xd4000001u, R(0)); // svc #0, encoding independent of flags/GPR.
        }
        private static Operand R(int index, OperandType type = OperandType.I64) => new(index, RegisterType.Integer, type);
        private static Operand C(ulong value) => new(OperandType.I64, value);
    }
}
