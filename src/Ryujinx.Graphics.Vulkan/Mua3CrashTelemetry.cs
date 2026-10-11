using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    internal enum Mua3CommandKind : byte
    {
        Draw,
        DrawIndexed,
        DrawIndirect,
        DrawIndexedIndirect,
        DrawIndirectCount,
        DrawIndexedIndirectCount,
        Dispatch,
        DispatchIndirect,
    }

    /// <summary>
    /// MUA3-only rolling telemetry. It maps the last graphics/compute commands recorded in each
    /// command buffer to the queue sequence and timeline value that submitted it. On DeviceLost,
    /// the matching submission and its immediate neighbours are printed before the process aborts.
    /// </summary>
    internal static class Mua3CrashTelemetry
    {
        private const int CommandsPerBuffer = 16;
        private const int SubmissionCapacity = 2048;

        private sealed class CommandBufferTrace
        {
            public int PoolIndex;
            public long Generation;
            public long TotalCommands;
            public int NextCommand;
            public int CommandCount;
            public readonly CommandTrace[] Commands = new CommandTrace[CommandsPerBuffer];
        }

        private readonly struct CommandTrace
        {
            public readonly long Serial;
            public readonly Mua3CommandKind Kind;
            public readonly ulong Pipeline;
            public readonly int PipelineHash;
            public readonly int ProgramId;
            public readonly uint ShaderStages;
            public readonly Mua3ShaderIdentity ShaderIdentity;
            public readonly int Topology;
            public readonly int Argument0;
            public readonly int Argument1;

            public CommandTrace(long serial, Mua3CommandKind kind, ulong pipeline, int pipelineHash,
                int programId, uint shaderStages, Mua3ShaderIdentity shaderIdentity,
                int topology, int argument0, int argument1)
            {
                Serial = serial;
                Kind = kind;
                Pipeline = pipeline;
                PipelineHash = pipelineHash;
                ProgramId = programId;
                ShaderStages = shaderStages;
                ShaderIdentity = shaderIdentity;
                Topology = topology;
                Argument0 = argument0;
                Argument1 = argument1;
            }
        }

        private sealed class SubmissionTrace
        {
            public ulong Sequence;
            public ulong Timeline;
            public ulong CommandBuffer;
            public int PoolIndex;
            public long Generation;
            public long TotalCommands;
            public CommandTrace[] Commands;
        }

        private static readonly object _sync = new();
        private static readonly Dictionary<ulong, CommandBufferTrace> _commandBuffers = new();
        private static readonly SubmissionTrace[] _submissions = new SubmissionTrace[SubmissionCapacity];

        private static long _commandSerial;
        private static long _commandBufferGeneration;
        private static int _enabledLogged;
        private static int _deviceLostLogged;

        private static bool Enabled => GraphicsCompatibility.IsMua3;

        private static ulong GetHandle(CommandBuffer commandBuffer)
        {
            return unchecked((ulong)commandBuffer.Handle);
        }

        public static void BeginCommandBuffer(CommandBuffer commandBuffer, int poolIndex)
        {
            if (!Enabled)
            {
                return;
            }

            if (Interlocked.Exchange(ref _enabledLogged, 1) == 0)
            {
                Logger.Info?.Print(LogClass.Gpu, "[MUA3-TRACE] Command-buffer submission telemetry enabled");
            }

            lock (_sync)
            {
                _commandBuffers[GetHandle(commandBuffer)] = new CommandBufferTrace
                {
                    PoolIndex = poolIndex,
                    Generation = Interlocked.Increment(ref _commandBufferGeneration),
                };
            }
        }

        public static void RecordCommand(CommandBuffer commandBuffer, Mua3CommandKind kind, ulong pipeline,
            int pipelineHash, int programId, uint shaderStages, Mua3ShaderIdentity shaderIdentity,
            int topology, int argument0, int argument1)
        {
            if (!Enabled)
            {
                return;
            }

            CommandTrace command = new(
                Interlocked.Increment(ref _commandSerial),
                kind,
                pipeline,
                pipelineHash,
                programId,
                shaderStages,
                shaderIdentity,
                topology,
                argument0,
                argument1);

            lock (_sync)
            {
                ulong commandBufferHandle = GetHandle(commandBuffer);

                if (!_commandBuffers.TryGetValue(commandBufferHandle, out CommandBufferTrace trace))
                {
                    trace = new CommandBufferTrace
                    {
                        PoolIndex = -1,
                        Generation = Interlocked.Increment(ref _commandBufferGeneration),
                    };
                    _commandBuffers.Add(commandBufferHandle, trace);
                }

                trace.Commands[trace.NextCommand] = command;
                trace.NextCommand = (trace.NextCommand + 1) % CommandsPerBuffer;
                trace.CommandCount = Math.Min(trace.CommandCount + 1, CommandsPerBuffer);
                trace.TotalCommands++;
            }
        }

        public static void RecordSubmission(CommandBuffer commandBuffer, ulong timeline, ulong sequence)
        {
            if (!Enabled || timeline == 0)
            {
                return;
            }

            lock (_sync)
            {
                ulong commandBufferHandle = GetHandle(commandBuffer);
                _commandBuffers.TryGetValue(commandBufferHandle, out CommandBufferTrace trace);

                SubmissionTrace submission = new()
                {
                    Sequence = sequence,
                    Timeline = timeline,
                    CommandBuffer = commandBufferHandle,
                    PoolIndex = trace?.PoolIndex ?? -1,
                    Generation = trace?.Generation ?? 0,
                    TotalCommands = trace?.TotalCommands ?? 0,
                    Commands = SnapshotCommands(trace),
                };

                _submissions[(int)(timeline % SubmissionCapacity)] = submission;
            }
        }

        private static CommandTrace[] SnapshotCommands(CommandBufferTrace trace)
        {
            if (trace == null || trace.CommandCount == 0)
            {
                return Array.Empty<CommandTrace>();
            }

            CommandTrace[] result = new CommandTrace[trace.CommandCount];
            int start = (trace.NextCommand - trace.CommandCount + CommandsPerBuffer) % CommandsPerBuffer;

            for (int i = 0; i < result.Length; i++)
            {
                result[i] = trace.Commands[(start + i) % CommandsPerBuffer];
            }

            return result;
        }

        public static void LogDeviceLost(string operation, ulong timeline, ulong sequence)
        {
            if (!Enabled || Interlocked.Exchange(ref _deviceLostLogged, 1) != 0)
            {
                return;
            }

            List<string> lines = new();

            lock (_sync)
            {
                lines.Add($"[MUA3-TRACE] DEVICE_LOST operation={operation} timeline={timeline} observedSequence={sequence}");

                ulong first = timeline > 6 ? timeline - 6 : 1;
                ulong last = timeline + 2;

                for (ulong value = first; value <= last; value++)
                {
                    SubmissionTrace submission = _submissions[(int)(value % SubmissionCapacity)];

                    if (submission == null || submission.Timeline != value)
                    {
                        continue;
                    }

                    string marker = value == timeline ? "FAULT" : "near";
                    lines.Add($"[MUA3-TRACE] {marker} timeline={submission.Timeline} sequence={submission.Sequence} cb=0x{submission.CommandBuffer:X} pool={submission.PoolIndex} generation={submission.Generation} totalCommands={submission.TotalCommands}");

                    foreach (CommandTrace command in submission.Commands)
                    {
                        lines.Add($"[MUA3-TRACE]   cmd={command.Serial} kind={command.Kind} pipeline=0x{command.Pipeline:X} pipelineHash=0x{command.PipelineHash:X8} program=0x{command.ProgramId:X8} stages=0x{command.ShaderStages:X} shaders=[{command.ShaderIdentity}] topology={command.Topology} arg0={command.Argument0} arg1={command.Argument1}");
                    }
                }
            }

            foreach (string line in lines)
            {
                Logger.Error?.Print(LogClass.Gpu, line);
            }
        }
    }
}
