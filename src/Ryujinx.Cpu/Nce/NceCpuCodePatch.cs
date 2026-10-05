using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Buffers.Binary;
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
        private readonly List<(int TextIndex, int PatchIndex, uint Instruction, uint[] Code)> _snapshotTargets = new();

        // NEOV2 NCE-EXCL1: instructions rewritten in place in the text section, without a trampoline.
        private readonly List<(int TextIndex, uint Instruction)> _inPlaceInstructions;

        /// <inheritdoc/>
        public ulong Size => BitUtils.AlignUp((ulong)_code.Count * sizeof(uint), 0x1000UL);

        public NceCpuCodePatch()
        {
            _code = new();
            _patchTargets = new();
            _inPlaceInstructions = new();
        }

        /// <summary>
        /// NEOV2 NCE-EXCL1: replaces one instruction of the text section with another of the same size.
        /// </summary>
        /// <param name="textIndex">Index of the instruction in the text section, in 32-bit words</param>
        /// <param name="instruction">Replacement instruction</param>
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

        internal void AddCode(int textIndex, IEnumerable<uint> code, uint instruction)
        {
            uint[] emitted = code is uint[] words ? words : new List<uint>(code).ToArray();
            _snapshotTargets.Add((textIndex, _code.Count, instruction, emitted));
            AddCode(textIndex, emitted);
        }

        /// <summary>Number of trampoline sites (excluding in-place rewrites) owned by this patch.</summary>
        public int TargetCount => _patchTargets.Count;

        /// <summary>
        /// NEOV2-NCE-FARPATCH1: a patch block placed just before the module can only serve text sites within
        /// the signed imm26 branch reach (+-128 MiB). For larger texts (SDK 22 titles have 185+ MiB of text),
        /// sites that are too far are moved to a second block, which the loader places right after the module image.
        /// Each trampoline is self-contained (internal labels plus the final return branch), so moving it is safe.
        /// Returns the far block, or null when every site is reachable from the block before the module.
        /// </summary>
        /// <param name="textOffset">Offset of the text section from the module base</param>
        public NceCpuCodePatch SplitFarTargets(ulong textOffset)
        {
            // Conservative: assume the trampoline may sit anywhere in a block as large as the whole patch.
            const ulong Reach = 0x7fffffcUL - 0x10000UL;
            ulong blockBound = Size + 0x1000UL;

            bool IsFar(PatchTarget target) => textOffset + (ulong)target.TextIndex * sizeof(uint) + sizeof(uint) + blockBound > Reach;

            bool anyFar = false;

            foreach (PatchTarget target in _patchTargets)
            {
                if (IsFar(target))
                {
                    anyFar = true;
                    break;
                }
            }

            if (!anyFar)
            {
                return null;
            }

            Dictionary<int, uint> snapshotInstructions = new(_snapshotTargets.Count);

            foreach (var snapshot in _snapshotTargets)
            {
                snapshotInstructions[snapshot.PatchIndex] = snapshot.Instruction;
            }

            PatchTarget[] targets = _patchTargets.ToArray();
            uint[] code = _code.ToArray();

            _code.Clear();
            _patchTargets.Clear();
            _snapshotTargets.Clear();

            NceCpuCodePatch far = new();

            foreach (PatchTarget target in targets)
            {
                uint[] chunk = code[target.PatchStartIndex..(target.PatchBranchIndex + 1)];
                NceCpuCodePatch destination = IsFar(target) ? far : this;

                if (snapshotInstructions.TryGetValue(target.PatchStartIndex, out uint instruction))
                {
                    destination.AddCode(target.TextIndex, chunk, instruction);
                }
                else
                {
                    destination.AddCode(target.TextIndex, chunk);
                }
            }

            return far;
        }

        /// <inheritdoc/>
        public void Write(IVirtualMemoryManager memoryManager, ulong patchAddress, ulong textAddress)
        {
            uint[] code = _code.ToArray();
            uint[] textBranches = new uint[_patchTargets.Count];

            // Preflight every relocation before the first write (including in-place rewrites).
            // Int32 overflow checks alone do not enforce the signed imm26, scaled-by-four range.
            for (int i = 0; i < _patchTargets.Count; i++)
            {
                PatchTarget target = _patchTargets[i];
                ulong patchStart = checked(patchAddress + (ulong)target.PatchStartIndex * sizeof(uint));
                ulong patchBranch = checked(patchAddress + (ulong)target.PatchBranchIndex * sizeof(uint));
                ulong textInstruction = checked(textAddress + (ulong)target.TextIndex * sizeof(uint));
                ulong textReturn = checked(textInstruction + sizeof(uint));

                textBranches[i] = EncodeBranch(textInstruction, patchStart);
                code[target.PatchBranchIndex] = EncodeBranch(patchBranch, textReturn);
            }

            foreach ((int textIndex, uint instruction) in _inPlaceInstructions)
            {
                memoryManager.Write(textAddress + (ulong)textIndex * sizeof(uint), instruction);
            }

            for (int i = 0; i < _patchTargets.Count; i++)
            {
                ulong address = checked(textAddress + (ulong)_patchTargets[i].TextIndex * sizeof(uint));
                memoryManager.Write(address, textBranches[i]);
            }

            if (Size != 0)
            {
                memoryManager.Write(patchAddress, MemoryMarshal.Cast<uint, byte>(code));
                memoryManager.Reprotect(patchAddress, Size, MemoryPermission.ReadAndExecute);
            }

            if (PlatformInfo.IsBionic)
            {
                NceSnapshotCodeMap map = NceSnapshotCodeMap.ForMemory(memoryManager);
                map.RegisterBlock(patchAddress, textAddress, _snapshotTargets);
            }
        }

        // Dynamic modules select their address before any source-alias write.
        public void ValidateAddresses(ulong patchAddress, ulong textAddress)
        {
            foreach (PatchTarget target in _patchTargets)
            {
                ulong start = checked(patchAddress + (ulong)target.PatchStartIndex * 4);
                ulong branch = checked(patchAddress + (ulong)target.PatchBranchIndex * 4);
                ulong instruction = checked(textAddress + (ulong)target.TextIndex * 4);
                EncodeBranch(instruction, start);
                EncodeBranch(branch, checked(instruction + 4));
            }
        }

        // Restore only words still owned by this patch, leaving unrelated guest writes intact.
        // The destination is the original heap alias after the module has been unmapped.
        public void RestoreText(IVirtualMemoryManager memory, ulong destination,
            ulong patchAddress, ulong textAddress, ReadOnlySpan<byte> originalText)
        {
            ValidateAddresses(patchAddress, textAddress);
            _ = checked(destination + (ulong)originalText.Length);
            foreach (PatchTarget target in _patchTargets)
                ValidateOriginalIndex(target.TextIndex, originalText.Length);
            foreach (var target in _inPlaceInstructions)
                ValidateOriginalIndex(target.TextIndex, originalText.Length);

            foreach (PatchTarget target in _patchTargets)
            {
                ulong offset = (ulong)target.TextIndex * 4;
                uint expected = EncodeBranch(checked(textAddress + offset),
                    checked(patchAddress + (ulong)target.PatchStartIndex * 4));
                RestoreWord(memory, checked(destination + offset), expected,
                    BinaryPrimitives.ReadUInt32LittleEndian(originalText.Slice(target.TextIndex * 4, 4)));
            }
            foreach (var target in _inPlaceInstructions)
            {
                RestoreWord(memory, checked(destination + (ulong)target.TextIndex * 4), target.Instruction,
                    BinaryPrimitives.ReadUInt32LittleEndian(originalText.Slice(target.TextIndex * 4, 4)));
            }
        }

        private static void ValidateOriginalIndex(int index, int length)
        {
            if (index < 0 || length < 4 || index > (length - 4) / 4)
                throw new ArgumentException("Original NRO text does not contain the patch site.");
        }

        private static void RestoreWord(IVirtualMemoryManager memory, ulong address, uint expected, uint original)
        {
            if (memory.Read<uint>(address) == expected) memory.Write(address, original);
        }

        private static ulong Distance(ulong first, ulong second) => first >= second ? first - second : second - first;

        private static uint EncodeBranch(ulong source, ulong target)
        {
            if (((source | target) & 3UL) != 0)
            {
                throw new ArgumentException($"NCE branch addresses must be four-byte aligned: 0x{source:X} -> 0x{target:X}.");
            }

            ulong distance = Distance(source, target);
            ulong limit = target >= source ? 0x7fffffcUL : 0x8000000UL;
            if (distance > limit)
            {
                throw new NotSupportedException($"NCE branch is out of imm26 range: 0x{source:X} -> 0x{target:X} ({distance} bytes). No patch instructions were published.");
            }

            long delta = target >= source ? (long)distance : -(long)distance;
            return 0x14000000u | ((uint)(delta >> 2) & 0x3ffffffu);
        }
    }
}
