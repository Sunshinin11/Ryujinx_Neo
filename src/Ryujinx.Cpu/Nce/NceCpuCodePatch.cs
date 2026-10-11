using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Native Code Execution CPU code patch.
    /// </summary>
    public class NceCpuCodePatch
    {
        private readonly List<uint> _code;

        private readonly struct PatchTarget
        {
            public readonly int TextIndex;
            public readonly int PatchStartIndex;
            public readonly int PatchBranchIndex;

            public PatchTarget(int textIndex, int patchStartIndex, int patchBranchIndex)
            {
                TextIndex = textIndex;
                PatchStartIndex = patchStartIndex;
                PatchBranchIndex = patchBranchIndex;
            }
        }

        private readonly List<PatchTarget> _patchTargets;

        // NEOV2 NCE-EXCL1: instruções substituídas diretamente no text.
        private readonly List<(int TextIndex, uint Instruction)> _inPlaceInstructions;

        /// <inheritdoc/>
        public ulong Size => BitUtils.AlignUp((ulong)_code.Count * sizeof(uint), 0x1000UL);

        public NceCpuCodePatch()
        {
            _code = new();
            _patchTargets = new();
            _inPlaceInstructions = new();
        }

        // NEOV2 NCE-EXCL1
        internal void ReplaceInstruction(int textIndex, uint instruction)
        {
            _inPlaceInstructions.Add((textIndex, instruction));
        }

        internal void AddCode(int textIndex, IEnumerable<uint> code)
        {
            int patchStartIndex = _code.Count;
            _code.AddRange(code);
            _patchTargets.Add(new PatchTarget(textIndex, patchStartIndex, _code.Count - 1));
        }

        /// <inheritdoc/>
        public void Write(
            IVirtualMemoryManager memoryManager,
            ulong lowerPatchAddress,
            ulong upperPatchAddress,
            ulong textAddress)
        {
            uint[] lowerCode = _code.ToArray();
            uint[] upperCode = _code.ToArray();
            int upperTargetCount = 0;

            foreach ((int textIndex, uint instruction) in _inPlaceInstructions)
            {
                memoryManager.Write(
                    textAddress + (ulong)textIndex * sizeof(uint),
                    instruction);
            }

            foreach (PatchTarget patchTarget in _patchTargets)
            {
                ulong instTextAddress = textAddress + (ulong)patchTarget.TextIndex * sizeof(uint);
                bool useLower = TryPreparePatch(
                    lowerCode,
                    patchTarget,
                    lowerPatchAddress,
                    instTextAddress,
                    out uint textBranch);

                if (!useLower && !TryPreparePatch(
                    upperCode,
                    patchTarget,
                    upperPatchAddress,
                    instTextAddress,
                    out textBranch))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(instTextAddress),
                        instTextAddress,
                        "NCE patch target is outside the AArch64 B range of both patch pools.");
                }

                if (!useLower)
                {
                    upperTargetCount++;
                }

                memoryManager.Write(instTextAddress, textBranch);
            }

            Logger.Info?.Print(
                LogClass.Cpu,
                $"[NEOV2-NCE-BRANGE2] lower={_patchTargets.Count - upperTargetCount} upper={upperTargetCount} poolSize=0x{Size:X}");

            if (Size != 0)
            {
                memoryManager.Write(lowerPatchAddress, MemoryMarshal.Cast<uint, byte>(lowerCode));
                memoryManager.Write(upperPatchAddress, MemoryMarshal.Cast<uint, byte>(upperCode));
                memoryManager.Reprotect(lowerPatchAddress, Size, MemoryPermission.ReadAndExecute);
                memoryManager.Reprotect(upperPatchAddress, Size, MemoryPermission.ReadAndExecute);
            }
        }

        private static bool TryPreparePatch(
            uint[] code,
            PatchTarget patchTarget,
            ulong patchAddress,
            ulong instTextAddress,
            out uint textBranch)
        {
            ulong instPatchStartAddress = patchAddress + (ulong)patchTarget.PatchStartIndex * sizeof(uint);
            ulong instPatchBranchAddress = patchAddress + (ulong)patchTarget.PatchBranchIndex * sizeof(uint);

            long branchToPatch = (long)instPatchStartAddress - (long)instTextAddress;
            long branchToText = (long)instTextAddress - (long)instPatchBranchAddress + sizeof(uint);

            if (!TryEncodeSImm26_2(branchToPatch, out uint encodedToPatch) ||
                !TryEncodeSImm26_2(branchToText, out uint encodedToText))
            {
                textBranch = 0;
                return false;
            }

            code[patchTarget.PatchBranchIndex] |= encodedToText;
            textBranch = 0x14000000u | encodedToPatch;
            return true;
        }

        private static bool TryEncodeSImm26_2(long value, out uint encoded)
        {
            const int MinValue = -0x08000000;
            const int MaxValue = 0x07fffffc;

            if ((value & 3) != 0 || value < MinValue || value > MaxValue)
            {
                encoded = 0;
                return false;
            }

            encoded = (uint)(value >> 2) & 0x3ffffff;
            return true;
        }
    }
}
