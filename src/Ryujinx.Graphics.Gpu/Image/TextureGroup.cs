using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Image
{
    /// <summary>
    /// An overlapping texture group with a given view compatibility.
    /// </summary>
    readonly struct TextureIncompatibleOverlap
    {
        public readonly TextureGroup Group;
        public readonly TextureViewCompatibility Compatibility;

        /// <summary>
        /// Create a new texture incompatible overlap.
        /// </summary>
        /// <param name="group">The group that is incompatible</param>
        /// <param name="compatibility">The view compatibility for the group</param>
        public TextureIncompatibleOverlap(TextureGroup group, TextureViewCompatibility compatibility)
        {
            Group = group;
            Compatibility = compatibility;
        }
    }

    /// <summary>
    /// A texture group represents a group of textures that belong to the same storage.
    /// When views are created, this class will track memory accesses for them separately.
    /// The group iteratively adds more granular tracking as views of different kinds are added.
    /// Note that a texture group can be absorbed into another when it becomes a view parent.
    /// </summary>
    class TextureGroup : IDisposable
    {
        /// <summary>
        /// Threshold of layers to force granular handles (and thus partial loading) on array/3D textures.
        /// </summary>
        private const int GranularLayerThreshold = 8;

        private delegate bool HandlesCallbackDelegate(int baseHandle, int regionCount, bool split = false, bool specialData = false);

        private readonly HandlesCallbackDelegate _signalModifyingCallback;
        private readonly HandlesCallbackDelegate _discardDataCallback;
        private readonly HandlesCallbackDelegate _checkDirtyCallback;

        /// <summary>
        /// The storage texture associated with this group.
        /// </summary>
        public Texture Storage { get; }

        /// <summary>
        /// Indicates if the texture has copy dependencies. If true, then all modifications
        /// must be signalled to the group, rather than skipping ones still to be flushed.
        /// </summary>
        public bool HasCopyDependencies { get; set; }

        /// <summary>
        /// Indicates if the texture group has a pre-emptive flush buffer.
        /// When one is present, the group must always be notified on unbind.
        /// </summary>
        public bool HasFlushBuffer => _flushBuffer != BufferHandle.Null;

        /// <summary>
        /// Indicates if this texture has any incompatible overlaps alive.
        /// </summary>
        public bool HasIncompatibleOverlaps => _incompatibleOverlaps.Count > 0;

        /// <summary>
        /// Number indicating the order this texture group was modified relative to others.
        /// </summary>
        public long ModifiedSequence { get; private set; }

        private readonly GpuContext _context;
        private readonly PhysicalMemory _physicalMemory;

        private int[] _allOffsets;
        private int[] _sliceSizes;
        private readonly bool _is3D;
        private readonly bool _isBuffer;
        private bool _hasMipViews;
        private bool _hasLayerViews;
        private readonly int _layers;
        private readonly int _levels;

        private MultiRange TextureRange => Storage.Range;

        /// <summary>
        /// The views array from the storage texture.
        /// </summary>
        private Texture[] _views;
        private TextureGroupHandle[] _handles;
        private bool[] _loadNeeded;

        /// <summary>
        /// Other texture groups that have incompatible overlaps with this one.
        /// </summary>
        private readonly List<TextureIncompatibleOverlap> _incompatibleOverlaps;
        private bool _incompatibleOverlapsDirty = true;
        private readonly bool _flushIncompatibleOverlaps;

        private BufferHandle _flushBuffer;
        private bool _flushBufferImported;
        private bool _flushBufferInvalid;

        /// <summary>
        /// Create a new texture group.
        /// </summary>
        /// <param name="context">GPU context that the texture group belongs to</param>
        /// <param name="physicalMemory">Physical memory where the <paramref name="storage"/> texture is mapped</param>
        /// <param name="storage">The storage texture for this group</param>
        /// <param name="incompatibleOverlaps">Groups that overlap with this one but are incompatible</param>
        public TextureGroup(GpuContext context, PhysicalMemory physicalMemory, Texture storage, List<TextureIncompatibleOverlap> incompatibleOverlaps)
        {
            Storage = storage;
            _context = context;
            _physicalMemory = physicalMemory;

            _is3D = storage.Info.Target == Target.Texture3D;
            _isBuffer = storage.Info.Target == Target.TextureBuffer;
            _layers = storage.Info.GetSlices();
            _levels = storage.Info.Levels;

            _incompatibleOverlaps = incompatibleOverlaps;
            _flushIncompatibleOverlaps = TextureCompatibility.IsFormatHostIncompatible(storage.Info, context.Capabilities);

            _signalModifyingCallback = SignalModifyingCallback;
            _discardDataCallback = DiscardDataCallback;
            _checkDirtyCallback = CheckDirtyCallback;
        }

        /// <summary>
        /// Initialize a new texture group's dirty regions and offsets.
        /// </summary>
        /// <param name="size">Size info for the storage texture</param>
        /// <param name="hasLayerViews">True if the storage will have layer views</param>
        /// <param name="hasMipViews">True if the storage will have mip views</param>
        public void Initialize(ref SizeInfo size, bool hasLayerViews, bool hasMipViews)
        {
            _allOffsets = size.AllOffsets;
            _sliceSizes = size.SliceSizes;

            bool eden3DSparse = false;

            if (_is3D && GraphicsConfig.AsyncLarge3DTextureUpload)
            {
                MultiRange sparseRange = Storage.Range;

                for (int i = 0; i < sparseRange.Count; i++)
                {
                    if (sparseRange.GetSubRange(i).Address == MemoryManager.PteUnmapped)
                    {
                        eden3DSparse = true;
                        break;
                    }
                }
            }

            if (eden3DSparse)
            {
                (_hasLayerViews, _hasMipViews) = PropagateGranularity(hasLayerViews, hasMipViews);
            }
            else if (Storage.Target.HasDepthOrLayers() && Storage.Info.GetSlices() > GranularLayerThreshold)
            {
                _hasLayerViews = true;
                _hasMipViews = true;
            }
            else
            {
                (_hasLayerViews, _hasMipViews) = PropagateGranularity(hasLayerViews, hasMipViews);

                // If the texture is partially mapped, fully subdivide handles immediately.

                MultiRange range = Storage.Range;
                for (int i = 0; i < range.Count; i++)
                {
                    if (range.GetSubRange(i).Address == MemoryManager.PteUnmapped)
                    {
                        _hasLayerViews = true;
                        _hasMipViews = true;

                        break;
                    }
                }
            }

            RecalculateHandleRegions();
        }

        /// <summary>
        /// Initializes all incompatible overlaps in the list, registers them with the other texture groups,
        /// and creates regular or exact raw byte copy dependencies where supported.
        /// </summary>
        public void InitializeOverlaps()
        {
            foreach (TextureIncompatibleOverlap overlap in _incompatibleOverlaps)
            {
                // LayoutIncompatible and better may still use a regular texture copy dependency.
                // Fully Incompatible pairs are not copy compatible in general, but may still qualify for an
                // exact raw byte copy dependency (checked internally by CreateCopyDependency) when they map
                // to exactly the same guest memory, such as differently typed/sized aliases of the same data.
                if (overlap.Compatibility <= TextureViewCompatibility.Incompatible)
                {
                    CreateCopyDependency(overlap.Group, false, overlap.Compatibility);
                }

                overlap.Group._incompatibleOverlaps.Add(new TextureIncompatibleOverlap(this, overlap.Compatibility));
                overlap.Group._incompatibleOverlapsDirty = true;
            }

            if (_incompatibleOverlaps.Count > 0)
            {
                SignalIncompatibleOverlapModified();
            }
        }

        /// <summary>
        /// Signal that the group is dirty to all views and the storage.
        /// </summary>
        private void SignalAllDirty()
        {
            Storage.SignalGroupDirty();
            if (_views != null)
            {
                foreach (Texture texture in _views)
                {
                    texture.SignalGroupDirty();
                }
            }
        }

        /// <summary>
        /// Signal that an incompatible overlap has been modified.
        /// If this group must flush incompatible overlaps, the group is signalled as dirty too.
        /// </summary>
        private void SignalIncompatibleOverlapModified()
        {
            _incompatibleOverlapsDirty = true;

            if (_flushIncompatibleOverlaps)
            {
                SignalAllDirty();
            }
        }

        /// <summary>
        /// Flushes incompatible overlaps if the storage format requires it, and they have been modified.
        /// This allows unsupported host formats to accept data written to format aliased textures.
        /// </summary>
        /// <returns>True if data was flushed, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FlushIncompatibleOverlapsIfNeeded()
        {
            if (_flushIncompatibleOverlaps && _incompatibleOverlapsDirty)
            {
                bool flushed = false;

                foreach (TextureIncompatibleOverlap overlap in _incompatibleOverlaps)
                {
                    flushed |= overlap.Group.Storage.FlushModified(true);
                }

                _incompatibleOverlapsDirty = false;

                return flushed;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Check and optionally consume the dirty flags for a given texture.
        /// The state is shared between views of the same layers and levels.
        /// </summary>
        /// <param name="texture">The texture being used</param>
        /// <param name="consume">True to consume the dirty flags and reprotect, false to leave them as is</param>
        /// <returns>True if a flag was dirty, false otherwise</returns>
        public bool CheckDirty(Texture texture, bool consume)
        {
            EvaluateRelevantHandles(texture, _checkDirtyCallback, out bool dirty, consume);

            return dirty;
        }
        
        bool CheckDirtyCallback(int baseHandle, int regionCount, bool split, bool consume)
        {
            bool dirty = false;

            for (int i = 0; i < regionCount; i++)
            {
                TextureGroupHandle group = _handles[baseHandle + i];

                foreach (RegionHandle handle in group.Handles)
                {
                    if (handle.Dirty)
                    {
                        if (consume)
                        {
                            handle.Reprotect();
                        }

                        dirty = true;
                    }
                }
            }

            return dirty;
        }

        /// <summary>
        /// Discards all data for a given texture.
        /// This clears all dirty flags and pending copies from other textures.
        /// </summary>
        /// <param name="texture">The texture being discarded</param>
        public void DiscardData(Texture texture)
        {
            EvaluateRelevantHandles(texture, _discardDataCallback, out _);
        }
        
        bool DiscardDataCallback(int baseHandle, int regionCount, bool split, bool bound)
        {
            for (int i = 0; i < regionCount; i++)
            {
                TextureGroupHandle group = _handles[baseHandle + i];

                group.DiscardData();
            }

            return true;
        }

        // Envio de texturas por faixas (sempre ligado).
        // Texturas 2D grandes (tambem com camadas e/ou mipmaps) ganham um rastreio extra da memoria:
        // - o nivel principal de cada camada e vigiado em pedacos pequenos (colunas de blocos do layout block-linear:
        //   cada coluna de uma fileira de blocos fica continua na memoria);
        // - os mipmaps menores de cada camada ficam num rastreio unico por camada.
        // Quando o jogo grava so um pedaco da textura, apenas os pedacos/mipmaps alterados sao reenviados para a GPU, em
        // vez da textura inteira. O rastreio normal da textura continua igual: isto so decide o que enviar no lugar do
        // envio inteiro, e em qualquer caso duvidoso o envio inteiro de sempre e usado.
        private RegionHandle[] _neoGranules; // [camada * _neoGranulesPerLayer + pedaco], nivel 0
        private RegionHandle[] _neoTails; // [camada], niveis 1..n (null sem mipmaps)
        private int[] _neoLayerOffset;
        private int _neoLayers;
        private int _neoBandsPerLayer;
        private int _neoBandBytes;
        private int _neoBandRows;
        private int _neoBandGobBlocksInY;
        private int _neoGobBlockBytes;
        private int _neoGranuleBytes;
        private int _neoGranulesPerLayer;
        private bool _neoReprotectAll;
        private long _neoTailBytes;
        private string _neoBandsRejected;

        private readonly List<(int Layer, int Offset, int Size)> _neoDirtyRanges = [];

        // Pedacos que ficaram sujos (avisados pelo rastreio de memoria na thread que escreveu).
        private readonly object _neoPendingLock = new();
        private List<int> _neoPending = [];
        private List<int> _neoWork = [];

        // conferencia por hash. Cada pedaco enviado guarda uma "impressao digital" do que foi enviado;
        // aos poucos (em rodizio, com limite de bytes por segundo) os pedacos marcados como sem mudanca sao conferidos.
        // Se o conteudo mudou sem o rastreio avisar, o pedaco e reenviado na hora e contado no log.
        private const int NeoVerifyMaxPerCall = 1024 * 1024;
        private const long NeoVerifyBytesPerSecond = 16L * 1024 * 1024;
        private const int NeoTailChunkBytes = 1024 * 1024;
        private const ulong NeoBandMinTextureSize = 4UL * 1024 * 1024;
        private Ryujinx.Common.Hash128[] _neoGranuleHash;
        private bool[] _neoGranuleHashKnown;
        private Ryujinx.Common.Hash128[] _neoTailHash; // [camada * _neoTailChunks + bloco]
        private bool[] _neoTailHashKnown;
        private int _neoTailChunks;
        private int _neoVerifyCursor;
        private long _neoVerifyCredit;
        private long _neoVerifyLast;

        private bool NeoFormatUploadsDirectly(Format format)
        {
            Capabilities caps = _context.Capabilities;

            if (format.IsDepthOrStencil())
            {
                return false;
            }

            if (format.IsAstc())
            {
                return caps.SupportsAstcCompression;
            }

            if (format.IsEtc2())
            {
                return caps.SupportsEtc2Compression;
            }

            if (!TextureCompatibility.HostSupportsBcFormat(format, Target.Texture2D, caps))
            {
                return false;
            }

            return format != Format.R4G4Unorm && format != Format.R4G4B4A4Unorm && !format.Is16BitPacked();
        }

        private string NeoBandIneligibleReason()
        {
            TextureInfo info = Storage.Info;

            if (_isBuffer || _is3D || (info.Target != Target.Texture2D && info.Target != Target.Texture2DArray))
            {
                return $"tipo {info.Target}";
            }

            if (info.IsLinear)
            {
                return "layout linear";
            }

            if (info.GobBlocksInTileX != 1 || info.GobBlocksInZ != 1)
            {
                return "layout de blocos incomum";
            }

            if (TextureRange.Count != 1)
            {
                return "memoria fragmentada";
            }

            if (Storage.ScaleFactor != 1f)
            {
                return "escala de resolucao";
            }

            if (Storage.HostTexture == null)
            {
                return "sem textura no host";
            }

            if (!NeoFormatUploadsDirectly(info.FormatInfo.Format))
            {
                return "formato precisa de conversao";
            }

            int bpp = info.FormatInfo.BytesPerPixel;

            if (bpp != 1 && bpp != 2 && bpp != 4 && bpp != 8 && bpp != 16)
            {
                return "bytes por pixel incomum";
            }

            return null;
        }

        private string NeoTryCreateBands()
        {
            const int TargetGranularity = 64 * 1024;
            const int MaxGranules = 64 * 1024;

            TextureInfo info = Storage.Info;
            FormatInfo format = info.FormatInfo;

            int layers = info.GetSlices();
            int levels = info.Levels;

            if (layers != _layers || levels != _levels || _allOffsets.Length != layers * levels)
            {
                return "camadas/niveis inconsistentes";
            }

            int bpp = format.BytesPerPixel;
            int wBlocks = Ryujinx.Common.BitUtils.DivRoundUp(info.Width, format.BlockWidth);
            int hBlocks = Ryujinx.Common.BitUtils.DivRoundUp(info.Height, format.BlockHeight);

            // Mesma reducao da altura do bloco que o LayoutConverter faz no nivel 0.
            int gobBlocksInY = info.GobBlocksInY;

            while (hBlocks <= (gobBlocksInY >> 1) * 8 && gobBlocksInY != 1)
            {
                gobBlocksInY >>= 1;
            }

            int bandRows = gobBlocksInY * 8;
            int gobBlockBytes = 512 * gobBlocksInY;
            int gobsWide = Ryujinx.Common.BitUtils.AlignUp(wBlocks * bpp, 64) / 64;
            long bandBytes = (long)gobsWide * gobBlockBytes;
            int bandsPerLayer = Ryujinx.Common.BitUtils.DivRoundUp(hBlocks, bandRows);

            if (bandsPerLayer * (long)gobsWide < 16)
            {
                return "poucos pedacos";
            }

            // Granularidade: algumas colunas de blocos (ate ~16 KiB), sem atravessar o fim de uma fileira.
            int columnsPerGranule = 1;

            while (gobBlockBytes * columnsPerGranule < TargetGranularity && gobsWide % (columnsPerGranule * 2) == 0)
            {
                columnsPerGranule *= 2;
            }

            int granularity = gobBlockBytes * columnsPerGranule;

            long level0Bytes = bandBytes * bandsPerLayer;
            int granulesPerLayer = (int)(level0Bytes / granularity);

            if ((long)granulesPerLayer * layers > MaxGranules)
            {
                return "pedacos demais";
            }

            long level0Size = levels > 1
                ? _allOffsets[GetOffsetIndex(0, 1)] - _allOffsets[GetOffsetIndex(0, 0)]
                : _sliceSizes[0];

            if (level0Bytes > level0Size || level0Bytes > _sliceSizes[0] || level0Bytes > int.MaxValue)
            {
                return "tamanho da memoria nao bate com as faixas";
            }

            long tailBytes = 0;

            if (levels > 1)
            {
                tailBytes = (long)_allOffsets[GetOffsetIndex(0, levels - 1)] + _sliceSizes[levels - 1] - _allOffsets[GetOffsetIndex(0, 1)];

                if (tailBytes <= 0)
                {
                    return "mipmaps com tamanho invalido";
                }
            }

            MemoryRange range = TextureRange.GetSubRange(0);

            if (range.Address == MemoryManager.PteUnmapped)
            {
                return "memoria desmapeada";
            }

            ulong[] layerAddress = new ulong[layers];
            int[] layerOffset = new int[layers];

            for (int layer = 0; layer < layers; layer++)
            {
                long layerEnd = levels > 1
                    ? (long)_allOffsets[GetOffsetIndex(layer, levels - 1)] + _sliceSizes[levels - 1]
                    : (long)_allOffsets[GetOffsetIndex(layer, 0)] + level0Bytes;

                if (layerEnd > (long)Storage.Size)
                {
                    return "tamanho da memoria nao bate com as camadas";
                }

                layerOffset[layer] = _allOffsets[GetOffsetIndex(layer, 0)];
                layerAddress[layer] = range.Address + (ulong)layerOffset[layer];
            }

            RegionHandle[] granules = new RegionHandle[layers * granulesPerLayer];
            RegionHandle[] tails = levels > 1 ? new RegionHandle[layers] : null;

            for (int layer = 0; layer < layers; layer++)
            {
                for (int i = 0; i < granulesPerLayer; i++)
                {
                    int index = layer * granulesPerLayer + i;

                    RegionHandle granule = _physicalMemory.BeginTracking(layerAddress[layer] + (ulong)((long)i * granularity), (ulong)granularity, ResourceKind.Texture);

                    // Cada vez que o pedaco passa de limpo para sujo, anota qual foi (sem precisar varrer todos depois).
                    granule.RegisterDirtyEvent(() => NeoOnGranuleDirty(index));

                    granules[index] = granule;
                }

                if (tails != null)
                {
                    ulong tailAddress = range.Address + (ulong)_allOffsets[GetOffsetIndex(layer, 1)];

                    tails[layer] = _physicalMemory.BeginTracking(tailAddress, (ulong)tailBytes, ResourceKind.Texture);
                }
            }

            _neoGranules = granules;
            _neoGranulesPerLayer = granulesPerLayer;
            _neoGranuleBytes = granularity;
            _neoReprotectAll = true; // Os pedacos nascem sujos sem aviso: a primeira carga protege todos.
            _neoTails = tails;
            _neoLayerOffset = layerOffset;
            _neoLayers = layers;
            _neoBandsPerLayer = bandsPerLayer;
            _neoBandBytes = (int)bandBytes;
            _neoBandRows = bandRows;
            _neoBandGobBlocksInY = gobBlocksInY;
            _neoGobBlockBytes = gobBlockBytes;
            _neoTailBytes = tailBytes;

            _neoGranuleHash = new Ryujinx.Common.Hash128[granules.Length];
            _neoGranuleHashKnown = new bool[granules.Length];
            _neoTailChunks = tails != null ? (int)((tailBytes + NeoTailChunkBytes - 1) / NeoTailChunkBytes) : 0;
            _neoTailHash = tails != null ? new Ryujinx.Common.Hash128[layers * _neoTailChunks] : null;
            _neoTailHashKnown = tails != null ? new bool[layers * _neoTailChunks] : null;
            _neoVerifyCursor = 0;
            _neoVerifyCredit = 0;
            _neoVerifyLast = System.Diagnostics.Stopwatch.GetTimestamp();

            return null;
        }

        private void NeoOnGranuleDirty(int index)
        {
            lock (_neoPendingLock)
            {
                _neoPending.Add(index);
            }
        }

        /// <summary>
        /// Pega a lista de pedacos avisados como sujos desde a ultima vez (a lista fica vazia para novos avisos).
        /// </summary>
        private List<int> NeoTakePending()
        {
            List<int> work = _neoWork;
            work.Clear();

            lock (_neoPendingLock)
            {
                _neoWork = _neoPending;
                _neoPending = work;
            }

            return _neoWork;
        }

        private void NeoReprotectBands()
        {
            if (_neoGranules != null)
            {
                if (_neoReprotectAll)
                {
                    _neoReprotectAll = false;
                    NeoTakePending();

                    foreach (RegionHandle granule in _neoGranules)
                    {
                        if (granule.Dirty)
                        {
                            granule.Reprotect();
                        }
                    }
                }
                else
                {
                    foreach (int index in NeoTakePending())
                    {
                        RegionHandle granule = _neoGranules[index];

                        if (granule.Dirty)
                        {
                            granule.Reprotect();
                        }
                    }
                }
            }

            if (_neoTails != null)
            {
                foreach (RegionHandle tail in _neoTails)
                {
                    if (tail.Dirty)
                    {
                        tail.Reprotect();
                    }
                }
            }
        }

        /// <summary>
        /// true quando a sincronizacao atual cobre a textura inteira (todas as camadas e niveis).
        /// </summary>
        private bool NeoCoversWholeStorage(Texture texture, int baseHandle, int regionCount)
        {
            return baseHandle == 0 &&
                   regionCount == _handles.Length &&
                   texture.FirstLayer == 0 &&
                   texture.FirstLevel == 0 &&
                   texture.Info.GetSlices() == _layers &&
                   texture.Info.Levels == _levels;
        }

        /// <summary>
        /// antes de um envio inteiro "de sempre", marca como enviados SO os pedacos que esse envio
        /// cobre (as camadas e niveis da textura/vista sincronizada). Pedacos de outras camadas ou niveis continuam
        /// pendentes e sao enviados na proxima vez (mesma regra do Ryujinx por camada/nivel).
        /// </summary>
        private void NeoReprotectBandsForScope(Texture texture, int baseHandle, int regionCount)
        {
            if (_neoGranules == null)
            {
                return;
            }

            if (NeoCoversWholeStorage(texture, baseHandle, regionCount))
            {
                NeoReprotectBands();
                return;
            }

            if (_neoReprotectAll)
            {
                // A primeira carga inteira ainda nao aconteceu: tudo continua pendente.
                return;
            }

            int firstLayer = texture.FirstLayer;
            int endLayer = firstLayer + texture.Info.GetSlices();
            int firstLevel = texture.FirstLevel;
            int endLevel = firstLevel + texture.Info.Levels;

            bool coversLevel0 = firstLevel == 0;
            bool coversTail = firstLevel <= 1 && endLevel >= _levels;

            List<int> keep = null;

            foreach (int index in NeoTakePending())
            {
                RegionHandle granule = _neoGranules[index];

                if (!granule.Dirty)
                {
                    continue;
                }

                int layer = index / _neoGranulesPerLayer;

                if (coversLevel0 && layer >= firstLayer && layer < endLayer)
                {
                    granule.Reprotect();
                }
                else
                {
                    (keep ??= []).Add(index);
                }
            }

            if (keep != null)
            {
                lock (_neoPendingLock)
                {
                    _neoPending.AddRange(keep);
                }
            }

            if (_neoTails != null && coversTail)
            {
                for (int layer = Math.Max(0, firstLayer); layer < Math.Min(endLayer, _neoLayers); layer++)
                {
                    if (_neoTails[layer].Dirty)
                    {
                        _neoTails[layer].Reprotect();
                    }
                }
            }
        }

        private void NeoDisposeBands()
        {
            if (_neoGranules != null)
            {
                foreach (RegionHandle granule in _neoGranules)
                {
                    granule.Dispose();
                }

                _neoGranules = null;

                lock (_neoPendingLock)
                {
                    _neoPending.Clear();
                }
            }

            if (_neoTails != null)
            {
                foreach (RegionHandle tail in _neoTails)
                {
                    tail.Dispose();
                }

                _neoTails = null;
            }

            _neoBandsRejected = null;

            _neoGranuleHash = null;
            _neoGranuleHashKnown = null;
            _neoTailHash = null;
            _neoTailHashKnown = null;
            _neoTailChunks = 0;
        }

        /// <summary>
        /// envia para a GPU as colunas [firstColumn, endColumn) de uma fileira de blocos de uma camada.
        /// </summary>
        /// <returns>Bytes enviados</returns>
        private int NeoUploadColumns(int layer, int band, int firstColumn, int endColumn, int wBlocks, int hBlocks)
        {
            TextureInfo info = Storage.Info;
            FormatInfo format = info.FormatInfo;

            int bpp = format.BytesPerPixel;
            int columnBlocks = 64 / bpp;
            int x0 = firstColumn * columnBlocks;
            int firstRow = band * _neoBandRows;

            int columns = endColumn - firstColumn;
            int byteOffset = _neoLayerOffset[layer] + band * _neoBandBytes + firstColumn * _neoGobBlockBytes;
            int byteSize = columns * _neoGobBlockBytes;

            ReadOnlySpan<byte> guest = _physicalMemory.GetSpan(TextureRange.Slice((ulong)byteOffset, (ulong)byteSize));

            // guarda a impressao digital exatamente do que esta sendo enviado.
            NeoStoreGranuleHashes(layer, byteOffset - _neoLayerOffset[layer], byteSize, guest);

            if (x0 >= wBlocks || firstRow >= hBlocks)
            {
                // So preenchimento de alinhamento, fora da imagem.
                return 0;
            }

            int pieceBlocks = columns * columnBlocks;
            int validBlocks = Math.Min(pieceBlocks, wBlocks - x0);
            int validRows = Math.Min(_neoBandRows, hBlocks - firstRow);

            MemoryOwner<byte> linear = LayoutConverter.ConvertBlockLinearToLinear(
                pieceBlocks * format.BlockWidth,
                _neoBandRows * format.BlockHeight,
                1,
                1,
                1,
                1,
                format.BlockWidth,
                format.BlockHeight,
                bpp,
                _neoBandGobBlocksInY,
                1,
                1,
                new SizeInfo(byteSize),
                guest);

            int srcStride = pieceBlocks * bpp;
            int dstStride = Ryujinx.Common.BitUtils.AlignUp(validBlocks * bpp, LayoutConverter.HostStrideAlignment);
            int dataBytes = dstStride * validRows;

            MemoryOwner<byte> data;

            if (validBlocks == pieceBlocks)
            {
                if (dataBytes < linear.Length)
                {
                    data = MemoryOwner<byte>.RentCopy(linear.Span[..dataBytes]);
                    linear.Dispose();
                }
                else
                {
                    data = linear;
                }
            }
            else
            {
                // Ultima coluna passa da borda da imagem: reempacota as linhas com a largura valida.
                data = MemoryOwner<byte>.Rent(dataBytes);

                Span<byte> src = linear.Span;
                Span<byte> dst = data.Span;
                int rowBytes = validBlocks * bpp;

                for (int row = 0; row < validRows; row++)
                {
                    src.Slice(row * srcStride, rowBytes).CopyTo(dst.Slice(row * dstStride, rowBytes));
                }

                linear.Dispose();
            }

            int x = x0 * format.BlockWidth;
            int y = firstRow * format.BlockHeight;
            int width = Math.Min(validBlocks * format.BlockWidth, info.Width - x);
            int height = Math.Min(validRows * format.BlockHeight, info.Height - y);

            Storage.HostTexture.SetData(data, layer, 0, new Rectangle<int>(x, y, width, height));

            return dataBytes;
        }

        /// <summary>
        /// guarda o hash dos pedacos inteiros contidos em [offsetInLayer, offsetInLayer + size) do nivel 0.
        /// </summary>
        private void NeoStoreGranuleHashes(int layer, int offsetInLayer, int size, ReadOnlySpan<byte> guest)
        {
            if (_neoGranuleHash == null || offsetInLayer % _neoGranuleBytes != 0 || size % _neoGranuleBytes != 0)
            {
                return;
            }

            int first = layer * _neoGranulesPerLayer + offsetInLayer / _neoGranuleBytes;
            int count = size / _neoGranuleBytes;

            for (int i = 0; i < count; i++)
            {
                _neoGranuleHash[first + i] = Ryujinx.Common.XXHash128.ComputeHash(guest.Slice(i * _neoGranuleBytes, _neoGranuleBytes));
                _neoGranuleHashKnown[first + i] = true;
            }
        }

        private Ryujinx.Common.Hash128 NeoHashGuest(int offset, int size)
        {
            return Ryujinx.Common.XXHash128.ComputeHash(_physicalMemory.GetSpan(TextureRange.Slice((ulong)offset, (ulong)size)));
        }

        private (int Offset, int Size) NeoTailChunk(int layer, int chunk)
        {
            long start = (long)chunk * NeoTailChunkBytes;
            int size = (int)Math.Min(NeoTailChunkBytes, _neoTailBytes - start);

            return (_allOffsets[GetOffsetIndex(layer, 1)] + (int)start, size);
        }

        /// <summary>
        /// marca a conferencia dos mipmaps de uma camada como "sem base" (a base e gravada na proxima conferencia).
        /// </summary>
        private void NeoForgetTailHashes(int layer)
        {
            if (_neoTailHashKnown == null)
            {
                return;
            }

            for (int chunk = 0; chunk < _neoTailChunks; chunk++)
            {
                _neoTailHashKnown[layer * _neoTailChunks + chunk] = false;
            }
        }

        /// <summary>
        /// envia os mipmaps (niveis 1..n) de uma camada.
        /// </summary>
        /// <returns>Bytes enviados</returns>
        private long NeoUploadTail(int layer)
        {
            long bytes = 0;

            for (int level = 1; level < _levels; level++)
            {
                int offset = _allOffsets[GetOffsetIndex(layer, level)];
                int size = _sliceSizes[level];

                ReadOnlySpan<byte> guest = _physicalMemory.GetSpan(TextureRange.Slice((ulong)offset, (ulong)size));

                MemoryOwner<byte> result = Storage.ConvertToHostCompatibleFormat(guest, level, true);

                bytes += result.Length;

                Storage.HostTexture.SetData(result, layer, level);
            }

            NeoForgetTailHashes(layer);

            return bytes;
        }

        /// <summary>
        /// depois de um envio inteiro "de sempre", grava a impressao digital do nivel principal das
        /// camadas enviadas (os mipmaps ganham a base aos poucos, na conferencia).
        /// </summary>
        private void NeoRehashAfterFull(Texture texture, int baseHandle, int regionCount)
        {
            if (_neoGranuleHash == null || _neoReprotectAll)
            {
                return;
            }

            int firstLayer;
            int endLayer;
            bool coversLevel0;
            bool coversTail;

            if (NeoCoversWholeStorage(texture, baseHandle, regionCount))
            {
                firstLayer = 0;
                endLayer = _neoLayers;
                coversLevel0 = true;
                coversTail = true;
            }
            else
            {
                firstLayer = Math.Max(0, texture.FirstLayer);
                endLayer = Math.Min(_neoLayers, texture.FirstLayer + texture.Info.GetSlices());
                coversLevel0 = texture.FirstLevel == 0;
                coversTail = texture.FirstLevel <= 1 && texture.FirstLevel + texture.Info.Levels >= _levels;
            }

            for (int layer = firstLayer; layer < endLayer; layer++)
            {
                if (coversLevel0)
                {
                    int level0Bytes = _neoGranulesPerLayer * _neoGranuleBytes;

                    ReadOnlySpan<byte> guest = _physicalMemory.GetSpan(TextureRange.Slice((ulong)_neoLayerOffset[layer], (ulong)level0Bytes));

                    NeoStoreGranuleHashes(layer, 0, level0Bytes, guest);
                }

                if (coversTail)
                {
                    NeoForgetTailHashes(layer);
                }
            }
        }

        /// <summary>
        /// confere, em rodizio, alguns pedacos marcados como sem mudanca. Se o conteudo mudou sem o
        /// rastreio avisar, reenvia o pedaco (ou os mipmaps da camada) e conta no log.
        /// </summary>
        private void NeoVerifySome()
        {
            if (_neoGranuleHash == null || _neoReprotectAll)
            {
                return;
            }

            foreach (TextureGroupHandle handle in _handles)
            {
                if (handle.Modified)
                {
                    // A GPU escreveu nesta textura: a memoria do jogo pode estar atrasada em relacao a ela.
                    return;
                }
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            long elapsed = Math.Min(start - _neoVerifyLast, System.Diagnostics.Stopwatch.Frequency);
            _neoVerifyLast = start;
            _neoVerifyCredit = Math.Min(NeoVerifyMaxPerCall, _neoVerifyCredit + elapsed * NeoVerifyBytesPerSecond / System.Diagnostics.Stopwatch.Frequency);

            if (_neoVerifyCredit < _neoGranuleBytes)
            {
                return;
            }

            int granuleCount = _neoGranules.Length;
            int units = granuleCount + (_neoTailHash != null ? _neoTailHash.Length : 0);

            TextureInfo info = Storage.Info;
            FormatInfo format = info.FormatInfo;
            int wBlocks = Ryujinx.Common.BitUtils.DivRoundUp(info.Width, format.BlockWidth);
            int hBlocks = Ryujinx.Common.BitUtils.DivRoundUp(info.Height, format.BlockHeight);

            int visited = 0;

            while (_neoVerifyCredit > 0 && visited < units)
            {
                int unit = _neoVerifyCursor;
                _neoVerifyCursor = unit + 1 >= units ? 0 : unit + 1;
                visited++;

                if (unit < granuleCount)
                {
                    RegionHandle granule = _neoGranules[unit];

                    if (granule.Dirty)
                    {
                        // Escrita avisada: o envio normal cuida.
                        continue;
                    }

                    int layer = unit / _neoGranulesPerLayer;
                    int offsetInLayer = (unit % _neoGranulesPerLayer) * _neoGranuleBytes;

                    Ryujinx.Common.Hash128 hash = NeoHashGuest(_neoLayerOffset[layer] + offsetInLayer, _neoGranuleBytes);
                    _neoVerifyCredit -= _neoGranuleBytes;

                    if (!_neoGranuleHashKnown[unit])
                    {
                        _neoGranuleHash[unit] = hash;
                        _neoGranuleHashKnown[unit] = true;
                        continue;
                    }

                    if (hash == _neoGranuleHash[unit] || granule.Dirty)
                    {
                        continue;
                    }

                    // Mudou sem aviso do rastreio.
                    int band = offsetInLayer / _neoBandBytes;
                    int firstColumn = (offsetInLayer - band * _neoBandBytes) / _neoGobBlockBytes;
                    int endColumn = firstColumn + _neoGranuleBytes / _neoGobBlockBytes;

                    NeoUploadColumns(layer, band, firstColumn, endColumn, wBlocks, hBlocks);
                }
                else
                {
                    int tailUnit = unit - granuleCount;
                    int layer = tailUnit / _neoTailChunks;
                    int chunk = tailUnit % _neoTailChunks;

                    if (_neoTails[layer].Dirty)
                    {
                        continue;
                    }

                    (int offset, int size) = NeoTailChunk(layer, chunk);

                    Ryujinx.Common.Hash128 hash = NeoHashGuest(offset, size);
                    _neoVerifyCredit -= size;

                    if (!_neoTailHashKnown[tailUnit])
                    {
                        _neoTailHash[tailUnit] = hash;
                        _neoTailHashKnown[tailUnit] = true;
                        continue;
                    }

                    if (hash == _neoTailHash[tailUnit] || _neoTails[layer].Dirty)
                    {
                        continue;
                    }

                    NeoUploadTail(layer);
                }
            }
        }

        /// <summary>
        /// tenta substituir o envio inteiro da textura pelo envio so dos pedacos/mipmaps alterados.
        /// Devolve false (com o motivo) para seguir o envio inteiro de sempre.
        /// </summary>
        private bool NeoTrySynchronizeBands(Texture texture, int baseHandle, int regionCount, out string reason)
        {
            if (Storage.Size < NeoBandMinTextureSize)
            {
                reason = "textura pequena";
                return false;
            }

            if (!NeoCoversWholeStorage(texture, baseHandle, regionCount))
            {
                // So quando o envio inteiro cobriria a textura toda (e nao so uma vista de parte dela).
                reason = "vista parcial da textura";
                return false;
            }

            reason = NeoBandIneligibleReason();

            if (reason != null)
            {
                return false;
            }

            if (_neoGranules == null)
            {
                if (_neoBandsRejected != null)
                {
                    reason = _neoBandsRejected;
                    return false;
                }

                reason = NeoTryCreateBands();

                if (reason != null)
                {
                    _neoBandsRejected = reason;
                    return false;
                }

                // O rastreio novo nasce todo marcado como alterado: a primeira carga e inteira.
                reason = "primeira carga (faixas criadas)";
                return false;
            }

            // Pega os pedacos alterados do nivel principal (e ja protege de novo antes de ler a memoria:
            // uma escrita durante o envio marca o pedaco outra vez).
            _neoDirtyRanges.Clear();

            if (_neoReprotectAll)
            {
                // Ainda nao houve a primeira carga inteira depois de criar os pedacos.
                reason = "primeira carga (faixas criadas)";
                return false;
            }

            long dirtyBytes = 0;

            List<int> pending = NeoTakePending();
            pending.Sort();

            int previous = -1;

            foreach (int index in pending)
            {
                if (index == previous)
                {
                    continue;
                }

                previous = index;

                RegionHandle granule = _neoGranules[index];

                if (!granule.Dirty)
                {
                    continue;
                }

                granule.Reprotect();

                int layer = index / _neoGranulesPerLayer;
                int offset = (index % _neoGranulesPerLayer) * _neoGranuleBytes;

                int last = _neoDirtyRanges.Count - 1;

                if (last >= 0 && _neoDirtyRanges[last].Layer == layer && _neoDirtyRanges[last].Offset + _neoDirtyRanges[last].Size == offset)
                {
                    _neoDirtyRanges[last] = (layer, _neoDirtyRanges[last].Offset, _neoDirtyRanges[last].Size + _neoGranuleBytes);
                }
                else
                {
                    _neoDirtyRanges.Add((layer, offset, _neoGranuleBytes));
                }

                dirtyBytes += _neoGranuleBytes;
            }

            int dirtyTails = 0;

            if (_neoTails != null)
            {
                foreach (RegionHandle tail in _neoTails)
                {
                    if (tail.Dirty)
                    {
                        dirtyTails++;
                        dirtyBytes += _neoTailBytes;
                    }
                }
            }

            if (dirtyBytes == 0)
            {
                // O rastreio geral da textura ficou marcado por uma escrita que o rastreio fino ja tinha pegado e
                // enviado (escrita durante o envio anterior). Nao ha nada novo para enviar.
                NeoVerifySome();
                reason = null;
                return true;
            }

            long trackedBytes = (long)_neoLayers * _neoBandsPerLayer * _neoBandBytes + (_neoTails != null ? _neoTails.Length * _neoTailBytes : 0);

            if (dirtyBytes * 4 >= trackedBytes * 3)
            {
                reason = "quase tudo alterado";
                return false;
            }

            TextureInfo info = Storage.Info;
            FormatInfo format = info.FormatInfo;

            int wBlocks = Ryujinx.Common.BitUtils.DivRoundUp(info.Width, format.BlockWidth);
            int hBlocks = Ryujinx.Common.BitUtils.DivRoundUp(info.Height, format.BlockHeight);

            int uploads = 0;
            long uploadedBytes = 0;

            foreach ((int layer, int rangeOffset, int rangeSize) in _neoDirtyRanges)
            {
                int offset = rangeOffset;
                int end = rangeOffset + rangeSize;

                while (offset < end)
                {
                    int band = offset / _neoBandBytes;
                    int bandStart = band * _neoBandBytes;
                    int pieceEnd = Math.Min(end, bandStart + _neoBandBytes);

                    int firstColumn = (offset - bandStart) / _neoGobBlockBytes;
                    int endColumn = Ryujinx.Common.BitUtils.DivRoundUp(pieceEnd - bandStart, _neoGobBlockBytes);

                    int bytes = NeoUploadColumns(layer, band, firstColumn, endColumn, wBlocks, hBlocks);

                    if (bytes != 0)
                    {
                        uploads++;
                        uploadedBytes += bytes;
                    }

                    offset = pieceEnd;
                }
            }

            _neoDirtyRanges.Clear();

            if (_neoTails != null && dirtyTails != 0)
            {
                for (int layer = 0; layer < _neoLayers; layer++)
                {
                    if (!_neoTails[layer].Dirty)
                    {
                        continue;
                    }

                    _neoTails[layer].Reprotect();

                    uploadedBytes += NeoUploadTail(layer);
                }
            }

            NeoVerifySome();

            return true;
        }

        /// <summary>
        /// Synchronize memory for a given texture.
        /// If overlapping tracking handles are dirty, fully or partially synchronize the texture data.
        /// </summary>
        /// <param name="texture">The texture being used</param>
        public void SynchronizeMemory(Texture texture)
        {
            FlushIncompatibleOverlapsIfNeeded();

            EvaluateRelevantHandles(texture, (baseHandle, regionCount, split, _) =>
            {
                bool dirty = false;
                bool anyModified = false;
                bool anyNotDirty = false;

                for (int i = 0; i < regionCount; i++)
                {
                    TextureGroupHandle group = _handles[baseHandle + i];

                    bool modified = group.Modified;
                    bool handleDirty = false;
                    bool handleUnmapped = false;

                    foreach (RegionHandle handle in group.Handles)
                    {
                        if (handle.Dirty)
                        {
                            handle.Reprotect();
                            handleDirty = true;
                        }
                        else
                        {
                            handleUnmapped |= handle.Unmapped;
                        }
                    }

                    // If the modified flag is still present, prefer the data written from gpu.
                    // A write from CPU will do a flush before writing its data, which should unset this.
                    if (modified)
                    {
                        handleDirty = false;
                    }

                    // Evaluate if any copy dependencies need to be fulfilled. A few rules:
                    // If the copy handle needs to be synchronized, prefer our own state.
                    // If we need to be synchronized and there is a copy present, prefer the copy.

                    if (group.NeedsCopy && group.Copy(_context))
                    {
                        anyModified |= true; // The copy target has been modified.
                        handleDirty = false;
                    }
                    else
                    {
                        anyModified |= modified;
                        dirty |= handleDirty;
                    }

                    if (group.NeedsCopy)
                    {
                        // The texture we copied from is still being written to. Copy from it again the next time this texture is used.
                        texture.SignalGroupDirty();
                    }

                    bool loadNeeded = handleDirty && !handleUnmapped;

                    anyNotDirty |= !loadNeeded;
                    _loadNeeded[baseHandle + i] = loadNeeded;
                }

                if (dirty)
                {
                    if (anyNotDirty || (_handles.Length > 1 && (anyModified || split)))
                    {
                        // Partial texture invalidation. Only update the layers/levels with dirty flags of the storage.

                        SynchronizePartial(baseHandle, regionCount);
                    }
                    else
                    {
                        // Full texture invalidation.
                        // Texturas grandes elegiveis enviam so as faixas alteradas.
                        if (!NeoTrySynchronizeBands(texture, baseHandle, regionCount, out string bandsSkipReason))
                        {
                            // So marca como enviados os pedacos que este envio inteiro cobre.
                            NeoReprotectBandsForScope(texture, baseHandle, regionCount);

                            texture.SynchronizeFull();

                            NeoRehashAfterFull(texture, baseHandle, regionCount);
                        }
                    }
                }

                return true;
            }, out _);
        }

        /// <summary>
        /// Synchronize part of the storage texture, represented by a given range of handles.
        /// Only handles marked by the _loadNeeded array will be synchronized.
        /// </summary>
        /// <param name="baseHandle">The base index of the range of handles</param>
        /// <param name="regionCount">The number of handles to synchronize</param>
        private void SynchronizePartial(int baseHandle, int regionCount)
        {
            int spanEndIndex = -1;
            int spanBase = 0;
            ReadOnlySpan<byte> dataSpan = ReadOnlySpan<byte>.Empty;

            for (int i = 0; i < regionCount; i++)
            {
                if (_loadNeeded[baseHandle + i])
                {
                    (int BaseLayer, int BaseLevel, int Levels, int Layers, int Index) info = GetHandleInformation(baseHandle + i);

                    // Ensure the data for this handle is loaded in the span.
                    if (spanEndIndex <= i - 1)
                    {
                        spanEndIndex = i;

                        if (_is3D)
                        {
                            // Look ahead to see how many handles need to be loaded.
                            for (int j = i + 1; j < regionCount; j++)
                            {
                                if (_loadNeeded[baseHandle + j])
                                {
                                    spanEndIndex = j;
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }

                        (int BaseLayer, int BaseLevel, int Levels, int Layers, int Index) endInfo = spanEndIndex == i ? info : GetHandleInformation(baseHandle + spanEndIndex);

                        spanBase = _allOffsets[info.Index];
                        int spanLast = _allOffsets[endInfo.Index + endInfo.Layers * endInfo.Levels - 1];
                        int endOffset = Math.Min(spanLast + _sliceSizes[endInfo.BaseLevel + endInfo.Levels - 1], (int)Storage.Size);
                        int size = endOffset - spanBase;

                        dataSpan = _physicalMemory.GetSpan(Storage.Range.Slice((ulong)spanBase, (ulong)size));
                    }

                    // Only one of these will be greater than 1, as partial sync is only called when there are sub-image views.
                    for (int layer = 0; layer < info.Layers; layer++)
                    {
                        for (int level = 0; level < info.Levels; level++)
                        {
                            int offsetIndex = GetOffsetIndex(info.BaseLayer + layer, info.BaseLevel + level);
                            int offset = _allOffsets[offsetIndex];

                            ReadOnlySpan<byte> data = dataSpan[(offset - spanBase)..];

                            MemoryOwner<byte> result = Storage.ConvertToHostCompatibleFormat(data, info.BaseLevel + level, true);

                            Storage.SetData(result, info.BaseLayer + layer, info.BaseLevel + level);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Synchronize dependent textures, if any of them have deferred a copy from the given texture.
        /// </summary>
        /// <param name="texture">The texture to synchronize dependents of</param>
        public void SynchronizeDependents(Texture texture)
        {
            EvaluateRelevantHandles(texture, (baseHandle, regionCount, _, _) =>
            {
                for (int i = 0; i < regionCount; i++)
                {
                    TextureGroupHandle group = _handles[baseHandle + i];

                    group.SynchronizeDependents();
                }

                return true;
            }, out _);
        }

        /// <summary>
        /// Determines whether flushes in this texture group should be tracked.
        /// Incompatible overlaps may need data from this texture to flush tracked for it to be visible to them.
        /// </summary>
        /// <returns>True if flushes should be tracked, false otherwise</returns>
        private bool ShouldFlushTriggerTracking()
        {
            foreach (TextureIncompatibleOverlap overlap in _incompatibleOverlaps)
            {
                if (overlap.Group._flushIncompatibleOverlaps)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets data from the host GPU, and flushes a slice to guest memory.
        /// </summary>
        /// <remarks>
        /// This method should be used to retrieve data that was modified by the host GPU.
        /// This is not cheap, avoid doing that unless strictly needed.
        /// When possible, the data is written directly into guest memory, rather than copied.
        /// </remarks>
        /// <param name="tracked">True if writing the texture data is tracked, false otherwise</param>
        /// <param name="sliceIndex">The index of the slice to flush</param>
        /// <param name="inBuffer">Whether the flushed texture data is up to date in the flush buffer</param>
        /// <param name="texture">The specific host texture to flush. Defaults to the storage texture</param>
        private void FlushTextureDataSliceToGuest(bool tracked, int sliceIndex, bool inBuffer, ITexture texture = null)
        {
            (int layer, int level) = GetLayerLevelForView(sliceIndex);

            int offset = _allOffsets[sliceIndex];
            int endOffset = Math.Min(offset + _sliceSizes[level], (int)Storage.Size);
            int size = endOffset - offset;

            using WritableRegion region = _physicalMemory.GetWritableRegion(Storage.Range.Slice((ulong)offset, (ulong)size), tracked);

            if (inBuffer)
            {
                using PinnedSpan<byte> data = _context.Renderer.GetBufferData(_flushBuffer, offset, size);

                Storage.ConvertFromHostCompatibleFormat(region.Memory.Span, data.Get(), level, true);
            }
            else
            {
                Storage.GetTextureDataSliceFromGpu(region.Memory.Span, layer, level, tracked, texture);
            }
        }

        /// <summary>
        /// Gets and flushes a number of slices of the storage texture to guest memory.
        /// </summary>
        /// <param name="tracked">True if writing the texture data is tracked, false otherwise</param>
        /// <param name="sliceStart">The first slice to flush</param>
        /// <param name="sliceEnd">The slice to finish flushing on (exclusive)</param>
        /// <param name="inBuffer">Whether the flushed texture data is up to date in the flush buffer</param>
        /// <param name="texture">The specific host texture to flush. Defaults to the storage texture</param>
        private void FlushSliceRange(bool tracked, int sliceStart, int sliceEnd, bool inBuffer, ITexture texture = null)
        {
            for (int i = sliceStart; i < sliceEnd; i++)
            {
                FlushTextureDataSliceToGuest(tracked, i, inBuffer, texture);
            }
        }

        /// <summary>
        /// Flush modified ranges for a given texture.
        /// </summary>
        /// <param name="texture">The texture being used</param>
        /// <param name="tracked">True if the flush writes should be tracked, false otherwise</param>
        /// <returns>True if data was flushed, false otherwise</returns>
        public bool FlushModified(Texture texture, bool tracked)
        {
            tracked = tracked || ShouldFlushTriggerTracking();
            bool flushed = false;

            EvaluateRelevantHandles(texture, (baseHandle, regionCount, split, _) =>
            {
                int startSlice = 0;
                int endSlice = 0;
                bool allModified = true;

                for (int i = 0; i < regionCount; i++)
                {
                    TextureGroupHandle group = _handles[baseHandle + i];

                    if (group.Modified)
                    {
                        if (endSlice < group.BaseSlice)
                        {
                            if (endSlice > startSlice)
                            {
                                FlushSliceRange(tracked, startSlice, endSlice, false);
                                flushed = true;
                            }

                            startSlice = group.BaseSlice;
                        }

                        endSlice = group.BaseSlice + group.SliceCount;

                        if (tracked)
                        {
                            group.Modified = false;

                            foreach (Texture texture in group.Overlaps)
                            {
                                texture.SignalModifiedDirty();
                            }
                        }
                    }
                    else
                    {
                        allModified = false;
                    }
                }

                if (endSlice > startSlice)
                {
                    if (allModified && !split)
                    {
                        texture.Flush(tracked);
                    }
                    else
                    {
                        FlushSliceRange(tracked, startSlice, endSlice, false);
                    }

                    flushed = true;
                }

                return true;
            }, out _);

            Storage.SignalModifiedDirty();

            return flushed;
        }

        /// <summary>
        /// Flush the texture data into a persistently mapped buffer.
        /// If the buffer does not exist, this method will create it.
        /// </summary>
        /// <param name="handle">Handle of the texture group to flush slices of</param>
        public void FlushIntoBuffer(TextureGroupHandle handle)
        {
            // Ensure that the buffer exists.

            if (_flushBufferInvalid && _flushBuffer != BufferHandle.Null)
            {
                _flushBufferInvalid = false;
                _context.Renderer.DeleteBuffer(_flushBuffer);
                _flushBuffer = BufferHandle.Null;
            }

            if (_flushBuffer == BufferHandle.Null)
            {
                if (!TextureCompatibility.CanTextureFlush(Storage.Info, _context.Capabilities))
                {
                    return;
                }

                bool canImport = Storage.Info.IsLinear && Storage.Info.Stride >= Storage.Info.Width * Storage.Info.FormatInfo.BytesPerPixel;

                nint hostPointer = canImport ? _physicalMemory.GetHostPointer(Storage.Range) : 0;

                if (hostPointer != 0 && _context.Renderer.PrepareHostMapping(hostPointer, Storage.Size))
                {
                    _flushBuffer = _context.Renderer.CreateBuffer(hostPointer, (int)Storage.Size);
                    _flushBufferImported = true;
                }
                else
                {
                    _flushBuffer = _context.Renderer.CreateBuffer((int)Storage.Size, BufferAccess.HostMemory);
                    _flushBufferImported = false;
                }

                Storage.BlacklistScale();
            }

            int sliceStart = handle.BaseSlice;
            int sliceEnd = sliceStart + handle.SliceCount;

            for (int i = sliceStart; i < sliceEnd; i++)
            {
                (int layer, int level) = GetLayerLevelForView(i);

                Storage.GetFlushTexture().CopyTo(new BufferRange(_flushBuffer, _allOffsets[i], _sliceSizes[level]), layer, level, _flushBufferImported ? Storage.Info.Stride : 0);
            }
        }

        /// <summary>
        /// Clears competing modified flags for all incompatible ranges, if they have possibly been modified.
        /// </summary>
        /// <param name="texture">The texture that has been modified</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearIncompatibleOverlaps(Texture texture)
        {
            if (_incompatibleOverlapsDirty)
            {
                foreach (TextureIncompatibleOverlap incompatible in _incompatibleOverlaps)
                {
                    incompatible.Group.ClearModified(texture.Range, this);

                    incompatible.Group.SignalIncompatibleOverlapModified();
                }

                _incompatibleOverlapsDirty = false;
            }
        }

        /// <summary>
        /// Signal that a texture in the group has been modified by the GPU.
        /// </summary>
        /// <param name="texture">The texture that has been modified</param>
        public void SignalModified(Texture texture)
        {
            ModifiedSequence = _context.GetModifiedSequence();

            ClearIncompatibleOverlaps(texture);

            EvaluateRelevantHandles(texture, (baseHandle, regionCount, _, _) =>
            {
                for (int i = 0; i < regionCount; i++)
                {
                    TextureGroupHandle group = _handles[baseHandle + i];

                    group.SignalModified(_context);
                }
                
                return true;
            }, out _);
        }

        /// <summary>
        /// Signal that a texture in the group is actively bound, or has been unbound by the GPU.
        /// </summary>
        /// <param name="texture">The texture that has been modified</param>
        /// <param name="bound">True if this texture is being bound, false if unbound</param>
        public void SignalModifying(Texture texture, bool bound)
        {
            ModifiedSequence = _context.GetModifiedSequence();

            ClearIncompatibleOverlaps(texture);
            
            EvaluateRelevantHandles(texture, _signalModifyingCallback, out _, bound);
        }
        
        bool SignalModifyingCallback(int baseHandle, int regionCount, bool split, bool bound)
        {
            for (int i = 0; i < regionCount; i++)
            {
                TextureGroupHandle group = _handles[baseHandle + i];

                group.SignalModifying(bound, _context);
            }
            
            return true;
        }

        /// <summary>
        /// Register a read/write action to flush for a texture group.
        /// </summary>
        /// <param name="group">The group to register an action for</param>
        public void RegisterAction(TextureGroupHandle group)
        {
            foreach (RegionHandle handle in group.Handles)
            {
                handle.RegisterAction((address, size) => FlushAction(group, address, size));
            }
        }

        /// <summary>
        /// Propagates the mip/layer view flags depending on the texture type.
        /// When the most granular type of subresource has views, the other type of subresource must be segmented granularly too.
        /// </summary>
        /// <param name="hasLayerViews">True if the storage has layer views</param>
        /// <param name="hasMipViews">True if the storage has mip views</param>
        /// <returns>The input values after propagation</returns>
        private (bool HasLayerViews, bool HasMipViews) PropagateGranularity(bool hasLayerViews, bool hasMipViews)
        {
            if (_is3D)
            {
                hasMipViews |= hasLayerViews;
            }
            else
            {
                hasLayerViews |= hasMipViews;
            }

            return (hasLayerViews, hasMipViews);
        }

        /// <summary>
        /// Evaluate the range of tracking handles which a view texture overlaps with.
        /// </summary>
        /// <param name="texture">The texture to get handles for</param>
        /// <param name="callback">
        /// A function to be called with the base index of the range of handles for the given texture, and the number of handles it covers.
        /// This can be called for multiple disjoint ranges, if required.
        /// </param>
        private void EvaluateRelevantHandles(Texture texture, HandlesCallbackDelegate callback, out bool result, bool specialData = false)
        {
            if (texture == Storage || !(_hasMipViews || _hasLayerViews))
            {
                result = callback(0, _handles.Length, specialData: specialData);

                return;
            }

            EvaluateRelevantHandles(texture.FirstLayer, texture.FirstLevel, texture.Info.GetSlices(), texture.Info.Levels, callback, out result, specialData);
        }

        /// <summary>
        /// Evaluate the range of tracking handles which a view texture overlaps with,
        /// using the view's position and slice/level counts.
        /// </summary>
        /// <param name="firstLayer">The first layer of the texture</param>
        /// <param name="firstLevel">The first level of the texture</param>
        /// <param name="slices">The slice count of the texture</param>
        /// <param name="levels">The level count of the texture</param>
        /// <param name="callback">
        /// A function to be called with the base index of the range of handles for the given texture, and the number of handles it covers.
        /// This can be called for multiple disjoint ranges, if required.
        /// </param>
        private void EvaluateRelevantHandles(int firstLayer, int firstLevel, int slices, int levels, HandlesCallbackDelegate callback, out bool result, bool specialData = false)
        {
            int targetLayerHandles = _hasLayerViews ? slices : 1;
            int targetLevelHandles = _hasMipViews ? levels : 1;

            result = false;
            
            if (_isBuffer)
            {
                return;
            }
            else if (_is3D)
            {
                // Future mip levels come after all layers of the last mip level. Each mipmap has less layers (depth) than the last.

                if (!_hasLayerViews)
                {
                    // When there are no layer views, the mips are at a consistent offset.

                    result = callback(firstLevel, targetLevelHandles, specialData: specialData);
                }
                else
                {
                    (int levelIndex, int layerCount) = Get3DLevelRange(firstLevel);

                    if (levels > 1 && slices < _layers)
                    {
                        // The given texture only covers some of the depth of multiple mips. (a "depth slice")
                        // Callback with each mip's range separately.
                        // Can assume that the group is fully subdivided (both slices and levels > 1 for storage)

                        while (levels-- > 1)
                        {
                            result = callback(firstLayer + levelIndex, slices, specialData: specialData);

                            levelIndex += layerCount;
                            layerCount = Math.Max(layerCount >> 1, 1);
                            slices = Math.Max(layerCount >> 1, 1);
                        }
                    }
                    else
                    {
                        int totalSize = Math.Min(layerCount, slices);

                        while (levels-- > 1)
                        {
                            layerCount = Math.Max(layerCount >> 1, 1);
                            totalSize += layerCount;
                        }

                        result = callback(firstLayer + levelIndex, totalSize, specialData: specialData);
                    }
                }
            }
            else
            {
                // Future layers come after all mipmaps of the last.
                int levelHandles = _hasMipViews ? _levels : 1;

                if (slices > 1 && levels < _levels)
                {
                    // The given texture only covers some of the mipmaps of multiple slices. (a "mip slice")
                    // Callback with each layer's range separately.
                    // Can assume that the group is fully subdivided (both slices and levels > 1 for storage)

                    for (int i = 0; i < slices; i++)
                    {
                        result = callback(firstLevel + (firstLayer + i) * levelHandles, targetLevelHandles, true, specialData: specialData);
                    }
                }
                else
                {
                    result = callback(firstLevel + firstLayer * levelHandles, targetLevelHandles + (targetLayerHandles - 1) * levelHandles, specialData: specialData);
                }
            }
        }

        /// <summary>
        /// Get the range of offsets for a given mip level of a 3D texture.
        /// </summary>
        /// <param name="level">The level to return</param>
        /// <returns>Start index and count of offsets for the given level</returns>
        private (int Index, int Count) Get3DLevelRange(int level)
        {
            int index = 0;
            int count = _layers; // Depth. Halves with each mip level.

            while (level-- > 0)
            {
                index += count;
                count = Math.Max(count >> 1, 1);
            }

            return (index, count);
        }

        /// <summary>
        /// Get view information for a single tracking handle.
        /// </summary>
        /// <param name="handleIndex">The index of the handle</param>
        /// <returns>The layers and levels that the handle covers, and its index in the offsets array</returns>
        private (int BaseLayer, int BaseLevel, int Levels, int Layers, int Index) GetHandleInformation(int handleIndex)
        {
            int baseLayer;
            int baseLevel;
            int levels = _hasMipViews ? 1 : _levels;
            int layers = _hasLayerViews ? 1 : _layers;
            int index;

            if (_is3D)
            {
                if (_hasLayerViews)
                {
                    // NOTE: Will also have mip views, or only one level in storage.

                    index = handleIndex;
                    baseLevel = 0;

                    int levelLayers = _layers;

                    while (handleIndex >= levelLayers)
                    {
                        handleIndex -= levelLayers;
                        baseLevel++;
                        levelLayers = Math.Max(levelLayers >> 1, 1);
                    }

                    baseLayer = handleIndex;
                }
                else
                {
                    baseLayer = 0;
                    baseLevel = handleIndex;

                    (index, _) = Get3DLevelRange(baseLevel);
                }
            }
            else
            {
                baseLevel = _hasMipViews ? handleIndex % _levels : 0;
                baseLayer = _hasMipViews ? handleIndex / _levels : handleIndex;
                index = baseLevel + baseLayer * _levels;
            }

            return (baseLayer, baseLevel, levels, layers, index);
        }

        /// <summary>
        /// Gets the layer and level for a given view.
        /// </summary>
        /// <param name="index">The index of the view</param>
        /// <returns>The layer and level of the specified view</returns>
        private (int BaseLayer, int BaseLevel) GetLayerLevelForView(int index)
        {
            if (_is3D)
            {
                int baseLevel = 0;

                int levelLayers = _layers;

                while (index >= levelLayers)
                {
                    index -= levelLayers;
                    baseLevel++;
                    levelLayers = Math.Max(levelLayers >> 1, 1);
                }

                return (index, baseLevel);
            }
            else
            {
                return (index / _levels, index % _levels);
            }
        }

        /// <summary>
        /// Find the byte offset of a given texture relative to the storage.
        /// </summary>
        /// <param name="texture">The texture to locate</param>
        /// <returns>The offset of the texture in bytes</returns>
        public int FindOffset(Texture texture)
        {
            return _allOffsets[GetOffsetIndex(texture.FirstLayer, texture.FirstLevel)];
        }

        /// <summary>
        /// Find the offset index of a given layer and level.
        /// </summary>
        /// <param name="layer">The view layer</param>
        /// <param name="level">The view level</param>
        /// <returns>The offset index of the given layer and level</returns>
        public int GetOffsetIndex(int layer, int level)
        {
            if (_is3D)
            {
                return layer + Get3DLevelRange(level).Index;
            }
            else
            {
                return level + layer * _levels;
            }
        }

        /// <summary>
        /// Generate a CpuRegionHandle for a given address and size range in CPU VA.
        /// </summary>
        /// <param name="address">The start address of the tracked region</param>
        /// <param name="size">The size of the tracked region</param>
        /// <returns>A CpuRegionHandle covering the given range</returns>
        private RegionHandle GenerateHandle(ulong address, ulong size)
        {
            return _physicalMemory.BeginTracking(address, size, ResourceKind.Texture);
        }

        /// <summary>
        /// Generate a TextureGroupHandle covering a specified range of views.
        /// </summary>
        /// <param name="viewStart">The start view of the handle</param>
        /// <param name="views">The number of views to cover</param>
        /// <returns>A TextureGroupHandle covering the given views</returns>
        private TextureGroupHandle GenerateHandles(int viewStart, int views)
        {
            int viewEnd = viewStart + views - 1;
            (_, int lastLevel) = GetLayerLevelForView(viewEnd);

            int offset = _allOffsets[viewStart];
            int endOffset = _allOffsets[viewEnd] + _sliceSizes[lastLevel];
            int size = endOffset - offset;

            List<RegionHandle> result = [];

            for (int i = 0; i < TextureRange.Count; i++)
            {
                MemoryRange item = TextureRange.GetSubRange(i);
                int subRangeSize = (int)item.Size;

                int sliceStart = Math.Clamp(offset, 0, subRangeSize);
                int sliceEnd = Math.Clamp(endOffset, 0, subRangeSize);

                if (sliceStart != sliceEnd && item.Address != MemoryManager.PteUnmapped)
                {
                    result.Add(GenerateHandle(item.Address + (ulong)sliceStart, (ulong)(sliceEnd - sliceStart)));
                }

                offset -= subRangeSize;
                endOffset -= subRangeSize;

                if (endOffset <= 0)
                {
                    break;
                }
            }

            (int firstLayer, int firstLevel) = GetLayerLevelForView(viewStart);

            if (_hasLayerViews && _hasMipViews)
            {
                size = _sliceSizes[firstLevel];
            }

            offset = _allOffsets[viewStart];
            ulong maxSize = Storage.Size - (ulong)offset;

            TextureGroupHandle groupHandle = new(
                this,
                offset,
                Math.Min(maxSize, (ulong)size),
                _views,
                firstLayer,
                firstLevel,
                viewStart,
                views,
                result.ToArray());

            return groupHandle;
        }

        /// <summary>
        /// Update the views in this texture group, rebuilding the memory tracking if required.
        /// </summary>
        /// <param name="views">The views list of the storage texture</param>
        /// <param name="texture">The texture that has been added, if that is the only change, otherwise null</param>
        public void UpdateViews(List<Texture> views, Texture texture)
        {
            // This is saved to calculate overlapping views for each handle.
            _views = views.ToArray();

            bool layerViews = _hasLayerViews;
            bool mipViews = _hasMipViews;
            bool regionsRebuilt = false;

            if (!(layerViews && mipViews))
            {
                foreach (Texture view in views)
                {
                    if (view.Info.GetSlices() < _layers)
                    {
                        layerViews = true;
                    }

                    if (view.Info.Levels < _levels)
                    {
                        mipViews = true;
                    }
                }

                (layerViews, mipViews) = PropagateGranularity(layerViews, mipViews);

                if (layerViews != _hasLayerViews || mipViews != _hasMipViews)
                {
                    _hasLayerViews = layerViews;
                    _hasMipViews = mipViews;

                    RecalculateHandleRegions();
                    regionsRebuilt = true;
                }
            }

            if (!regionsRebuilt)
            {
                if (texture != null)
                {
                    int offset = FindOffset(texture);

                    foreach (TextureGroupHandle handle in _handles)
                    {
                        handle.AddOverlap(offset, texture);
                    }
                }
                else
                {
                    // Must update the overlapping views on all handles, but only if they were not just recreated.

                    foreach (TextureGroupHandle handle in _handles)
                    {
                        handle.RecalculateOverlaps(this, views);
                    }
                }
            }

            SignalAllDirty();
        }

        /// <summary>
        /// Removes a view from the group, removing it from all overlap lists.
        /// </summary>
        /// <param name="views">The views list of the storage texture</param>
        /// <param name="view">View to remove from the group</param>
        public void RemoveView(List<Texture> views, Texture view)
        {
            // This is saved to calculate overlapping views for each handle.
            _views = views.ToArray();

            int offset = FindOffset(view);

            foreach (TextureGroupHandle handle in _handles)
            {
                handle.RemoveOverlap(offset, view);
            }
        }

        /// <summary>
        /// Inherit handle state from an old set of handles, such as modified and dirty flags.
        /// </summary>
        /// <param name="oldHandles">The set of handles to inherit state from</param>
        /// <param name="handles">The set of handles inheriting the state</param>
        /// <param name="relativeOffset">The offset of the old handles in relation to the new ones</param>
        private void InheritHandles(TextureGroupHandle[] oldHandles, TextureGroupHandle[] handles, int relativeOffset)
        {
            foreach (TextureGroupHandle group in handles)
            {
                foreach (RegionHandle handle in group.Handles)
                {
                    bool dirty = false;

                    foreach (TextureGroupHandle oldGroup in oldHandles)
                    {
                        if (group.OverlapsWith(oldGroup.Offset + relativeOffset, oldGroup.Size))
                        {
                            foreach (RegionHandle oldHandle in oldGroup.Handles)
                            {
                                if (handle.OverlapsWith(oldHandle.Address, oldHandle.Size))
                                {
                                    dirty |= oldHandle.Dirty;
                                }
                            }

                            group.Inherit(oldGroup, group.Offset == oldGroup.Offset + relativeOffset);
                        }
                    }

                    if (dirty && !handle.Dirty)
                    {
                        handle.Reprotect(true);
                    }

                    if (group.Modified)
                    {
                        handle.RegisterAction((address, size) => FlushAction(group, address, size));
                    }
                }
            }

            foreach (TextureGroupHandle oldGroup in oldHandles)
            {
                oldGroup.Modified = false;
            }
        }

        /// <summary>
        /// Inherit state from another texture group.
        /// </summary>
        /// <param name="other">The texture group to inherit from</param>
        public void Inherit(TextureGroup other)
        {
            bool layerViews = _hasLayerViews || other._hasLayerViews;
            bool mipViews = _hasMipViews || other._hasMipViews;

            if (layerViews != _hasLayerViews || mipViews != _hasMipViews)
            {
                _hasLayerViews = layerViews;
                _hasMipViews = mipViews;

                RecalculateHandleRegions();
            }

            foreach (TextureIncompatibleOverlap incompatible in other._incompatibleOverlaps)
            {
                RegisterIncompatibleOverlap(incompatible, false);

                incompatible.Group._incompatibleOverlaps.RemoveAll(overlap => overlap.Group == other);
            }

            int relativeOffset = Storage.Range.FindOffset(other.Storage.Range);

            InheritHandles(other._handles, _handles, relativeOffset);
        }

        /// <summary>
        /// Replace the current handles with the new handles. It is assumed that the new handles start dirty.
        /// The dirty flags from the previous handles will be kept.
        /// </summary>
        /// <param name="handles">The handles to replace the current handles with</param>
        /// <param name="rangeChanged">True if the storage memory range changed since the last region handle generation</param>
        private void ReplaceHandles(TextureGroupHandle[] handles, bool rangeChanged)
        {
            if (_handles != null)
            {
                // When replacing handles, they should start as non-dirty.

                foreach (TextureGroupHandle groupHandle in handles)
                {
                    if (rangeChanged)
                    {
                        // When the storage range changes, this becomes a little different.
                        // If a range does not match one in the original, treat it as modified.
                        // It has been newly mapped and its data must be synchronized.

                        if (groupHandle.Handles.Length == 0)
                        {
                            continue;
                        }

                        foreach (TextureGroupHandle oldGroup in _handles)
                        {
                            if (!groupHandle.OverlapsWith(oldGroup.Offset, oldGroup.Size))
                            {
                                continue;
                            }

                            foreach (RegionHandle handle in groupHandle.Handles)
                            {
                                bool hasMatch = false;

                                foreach (RegionHandle oldHandle in oldGroup.Handles)
                                {
                                    if (oldHandle.RangeEquals(handle))
                                    {
                                        hasMatch = true;
                                        break;
                                    }
                                }

                                if (hasMatch)
                                {
                                    handle.Reprotect();
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (RegionHandle handle in groupHandle.Handles)
                        {
                            handle.Reprotect();
                        }
                    }
                }

                InheritHandles(_handles, handles, 0);

                foreach (TextureGroupHandle oldGroup in _handles)
                {
                    foreach (RegionHandle oldHandle in oldGroup.Handles)
                    {
                        oldHandle.Dispose();
                    }
                }
            }

            _handles = handles;
            _loadNeeded = new bool[_handles.Length];
        }

        /// <summary>
        /// Recalculate handle regions for this texture group, and inherit existing state into the new handles.
        /// </summary>
        /// <param name="rangeChanged">True if the storage memory range changed since the last region handle generation</param>
        private void RecalculateHandleRegions(bool rangeChanged = false)
        {
            TextureGroupHandle[] handles;

            if (_isBuffer)
            {
                handles = [];
            }
            else if (!(_hasMipViews || _hasLayerViews))
            {
                // Single dirty region.
                RegionHandle[] cpuRegionHandles = new RegionHandle[TextureRange.Count];
                int count = 0;

                for (int i = 0; i < TextureRange.Count; i++)
                {
                    MemoryRange currentRange = TextureRange.GetSubRange(i);
                    if (currentRange.Address != MemoryManager.PteUnmapped)
                    {
                        cpuRegionHandles[count++] = GenerateHandle(currentRange.Address, currentRange.Size);
                    }
                }

                if (count != TextureRange.Count)
                {
                    Array.Resize(ref cpuRegionHandles, count);
                }

                TextureGroupHandle groupHandle = new(this, 0, Storage.Size, _views, 0, 0, 0, _allOffsets.Length, cpuRegionHandles);

                handles = [groupHandle];
            }
            else
            {
                // Get views for the host texture.
                // It's worth noting that either the texture has layer views or mip views when getting to this point, which simplifies the logic a little.
                // Depending on if the texture is 3d, either the mip views imply that layer views are present (2d) or the other way around (3d).
                // This is enforced by the way the texture matched as a view, so we don't need to check.

                int layerHandles = _hasLayerViews ? _layers : 1;
                int levelHandles = _hasMipViews ? _levels : 1;

                int handleIndex = 0;

                if (_is3D)
                {
                    List<TextureGroupHandle> handlesList = [];

                    for (int i = 0; i < levelHandles; i++)
                    {
                        for (int j = 0; j < layerHandles; j++)
                        {
                            (int viewStart, int views) = Get3DLevelRange(i);
                            viewStart += j;
                            views = _hasLayerViews ? 1 : views; // A layer view is also a mip view.

                            handlesList.Add(GenerateHandles(viewStart, views));
                        }

                        layerHandles = Math.Max(1, layerHandles >> 1);
                    }

                    handles = handlesList.ToArray();
                }
                else
                {
                    handles = new TextureGroupHandle[layerHandles * levelHandles];

                    for (int i = 0; i < layerHandles; i++)
                    {
                        for (int j = 0; j < levelHandles; j++)
                        {
                            int viewStart = j + i * _levels;
                            int views = _hasMipViews ? 1 : _levels; // A mip view is also a layer view.

                            handles[handleIndex++] = GenerateHandles(viewStart, views);
                        }
                    }
                }
            }

            ReplaceHandles(handles, rangeChanged);
        }

        /// <summary>
        /// Regenerates handles when the storage range has been remapped.
        /// This forces the regions to be fully subdivided.
        /// </summary>
        public void RangeChanged()
        {
            NeoDisposeBands();

            bool eden3D = _is3D && GraphicsConfig.AsyncLarge3DTextureUpload;

            _hasLayerViews = !eden3D;
            _hasMipViews = !eden3D;

            RecalculateHandleRegions(true);

            SignalAllDirty();
        }

        /// <summary>
        /// Ensure that there is a handle for each potential texture view. Required for copy dependencies to work.
        /// </summary>
        private void EnsureFullSubdivision()
        {
            if (!(_hasLayerViews && _hasMipViews))
            {
                _hasLayerViews = true;
                _hasMipViews = true;

                RecalculateHandleRegions();
            }
        }

        /// <summary>
        /// Create a copy dependency between this texture group, and a texture at a given layer/level offset.
        /// </summary>
        /// <param name="other">The view compatible texture to create a dependency to</param>
        /// <param name="firstLayer">The base layer of the given texture relative to the storage</param>
        /// <param name="firstLevel">The base level of the given texture relative to the storage</param>
        /// <param name="copyTo">True if this texture is first copied to the given one, false for the opposite direction</param>
        public void CreateCopyDependency(Texture other, int firstLayer, int firstLevel, bool copyTo)
        {
            TextureGroup otherGroup = other.Group;

            EnsureFullSubdivision();
            otherGroup.EnsureFullSubdivision();

            // Get the location of each texture within its storage, so we can find the handles to apply the dependency to.
            // This can consist of multiple disjoint regions, for example if this is a mip slice of an array texture.

            List<(int BaseHandle, int RegionCount)> targetRange = [];
            List<(int BaseHandle, int RegionCount)> otherRange = [];

            EvaluateRelevantHandles(firstLayer, firstLevel, other.Info.GetSlices(), other.Info.Levels, (baseHandle, regionCount, _, _) =>
            {
                targetRange.Add((baseHandle, regionCount));
                return true;
            }, out _);
            otherGroup.EvaluateRelevantHandles(other, (baseHandle, regionCount, _, _) =>
            {
                otherRange.Add((baseHandle, regionCount));
                return true;
            }, out _);

            int targetIndex = 0;
            int otherIndex = 0;
            (int Handle, int RegionCount) targetRegion = (0, 0);
            (int Handle, int RegionCount) otherRegion = (0, 0);

            while (true)
            {
                if (targetRegion.RegionCount == 0)
                {
                    if (targetIndex >= targetRange.Count)
                    {
                        break;
                    }

                    targetRegion = targetRange[targetIndex++];
                }

                if (otherRegion.RegionCount == 0)
                {
                    if (otherIndex >= otherRange.Count)
                    {
                        break;
                    }

                    otherRegion = otherRange[otherIndex++];
                }

                TextureGroupHandle handle = _handles[targetRegion.Handle++];
                TextureGroupHandle otherHandle = other.Group._handles[otherRegion.Handle++];

                targetRegion.RegionCount--;
                otherRegion.RegionCount--;

                handle.CreateCopyDependency(otherHandle, copyTo);

                // If "copyTo" is true, this texture must copy to the other.
                // Otherwise, it must copy to this texture.

                if (copyTo)
                {
                    otherHandle.Copy(_context, handle);
                }
                else
                {
                    handle.Copy(_context, otherHandle);
                }
            }
        }

        /// <summary>
        /// Determines whether an exact raw byte copy dependency can be safely created
        /// between this texture group and another fully incompatible texture group.
        /// </summary>
        /// <param name="other">The overlapping texture group to evaluate</param>
        /// <returns>
        /// True if both texture groups describe the same contiguous guest memory range
        /// and have compatible logical payload sizes and resource properties for an exact raw byte copy;
        /// otherwise, false
        /// </returns>
        private bool CanCreateRawCopyDependency(TextureGroup other)
        {
            TextureInfo info = Storage.Info;
            TextureInfo otherInfo = other.Storage.Info;
            long size = (long)info.Width * info.Height * info.GetDepth() * info.FormatInfo.BytesPerPixel;
            long otherSize = (long)otherInfo.Width * otherInfo.Height * otherInfo.GetDepth() * otherInfo.FormatInfo.BytesPerPixel;

            return size > 0 &&
                size <= int.MaxValue &&
                size == otherSize &&
                Storage.Range.Count == 1 &&
                other.Storage.Range.Count == 1 &&
                Storage.Range.GetSubRange(0).Address == other.Storage.Range.GetSubRange(0).Address &&
                Storage.Range.GetSubRange(0).Size == other.Storage.Range.GetSubRange(0).Size &&
                info.Levels == 1 &&
                otherInfo.Levels == 1 &&
                info.GetSlices() == 1 &&
                otherInfo.GetSlices() == 1 &&
                info.Samples == 1 &&
                otherInfo.Samples == 1 &&
                info.Target != Target.TextureBuffer &&
                otherInfo.Target != Target.TextureBuffer &&
                !info.FormatInfo.IsCompressed &&
                !otherInfo.FormatInfo.IsCompressed &&
                !info.FormatInfo.Format.IsDepthOrStencil() &&
                !otherInfo.FormatInfo.Format.IsDepthOrStencil();
        }

        /// <summary>
        /// Creates copy dependencies between overlapping handles in this texture group
        /// and another texture group.
        /// Regular texture copy dependencies are created for compatible handle pairs.
        /// For fully incompatible groups, an exact raw byte copy dependency may be created
        /// when the strict raw copy requirements are satisfied.
        /// </summary>
        /// <param name="other">The texture group that overlaps this one</param>
        /// <param name="copyTo">True if this texture should initially be copied to the other texture; false if the other texture should initially be copied to this texture</param>
        /// <param name="compatibility">The view compatibility between the two texture groups, used to determine whether a regular texture copy or an exact raw byte copy may be used</param>
        public void CreateCopyDependency(TextureGroup other, bool copyTo,
            TextureViewCompatibility compatibility = TextureViewCompatibility.LayoutIncompatible)
        {
            bool rawCopy = compatibility == TextureViewCompatibility.Incompatible && CanCreateRawCopyDependency(other);
            for (int i = 0; i < _allOffsets.Length; i++)
            {
                (_, int level) = GetLayerLevelForView(i);
                MultiRange handleRange = Storage.Range.Slice((ulong)_allOffsets[i], 1);
                ulong handleBase = handleRange.GetSubRange(0).Address;

                for (int j = 0; j < other._handles.Length; j++)
                {
                    (_, int otherLevel) = other.GetLayerLevelForView(j);
                    MultiRange otherHandleRange = other.Storage.Range.Slice((ulong)other._allOffsets[j], 1);
                    ulong otherHandleBase = otherHandleRange.GetSubRange(0).Address;

                    if (handleBase == otherHandleBase)
                    {
                        // Check if the two sizes are compatible.
                        TextureInfo info = Storage.Info;
                        TextureInfo otherInfo = other.Storage.Info;

                        // ViewLayoutCompatible/CopySizeMatches only reason about textures with some genuine
                        // format relationship (LayoutIncompatible or better) - they are not aware of, and must
                        // never be used to justify, a plain texture-to-texture copy (which some backends
                        // implement via a reinterpreting view) between fully Incompatible aliases such as a
                        // depth format and an unrelated color format. For Incompatible pairs, only the strict
                        // raw byte copy dependency below (which explicitly excludes depth/stencil formats) may
                        // be used.
                        bool textureCopy = compatibility != TextureViewCompatibility.Incompatible &&
                            TextureCompatibility.ViewLayoutCompatible(info, otherInfo, level, otherLevel) &&
                            TextureCompatibility.CopySizeMatches(info, otherInfo, level, otherLevel);

                        if (textureCopy || rawCopy)
                        {
                            // These textures are copy compatible. Create the dependency.

                            EnsureFullSubdivision();
                            other.EnsureFullSubdivision();

                            TextureGroupHandle handle = _handles[i];
                            TextureGroupHandle otherHandle = other._handles[j];

                            handle.CreateCopyDependency(otherHandle, copyTo, rawCopy);

                            // If "copyTo" is true, this texture must copy to the other.
                            // Otherwise, it must copy to this texture.

                            if (copyTo)
                            {
                                if (rawCopy)
                                {
                                    otherHandle.DeferCopy(handle, true);
                                    otherHandle.Copy(_context);
                                }
                                else
                                {
                                    otherHandle.Copy(_context, handle);
                                }
                            }
                            else
                            {
                                if (rawCopy)
                                {
                                    handle.DeferCopy(otherHandle, true);
                                    handle.Copy(_context);
                                }
                                else
                                {
                                    handle.Copy(_context, otherHandle);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Registers another texture group as an incompatible overlap, if it has not already been registered,
        /// and creates a supported copy dependency when requested.
        /// </summary>
        /// <param name="other">The incompatible texture overlap to register</param>
        /// <param name="copy">True to attempt to create a regular or exact raw byte copy dependency for the overlapping texture groups; otherwise, false</param>
        public void RegisterIncompatibleOverlap(TextureIncompatibleOverlap other, bool copy)
        {
            if (!_incompatibleOverlaps.Any(overlap => overlap.Group == other.Group))
            {
                if (copy && other.Compatibility <= TextureViewCompatibility.Incompatible)
                {
                    // Any of the group's views may share compatibility, even if the parents do not fully.
                    // Fully Incompatible groups are also let through here, since CreateCopyDependency will
                    // fall back to an exact raw byte copy dependency for them when the strict requirements
                    // for one are met (see CanCreateRawCopyDependency).
                    CreateCopyDependency(other.Group, false, other.Compatibility);
                }

                _incompatibleOverlaps.Add(other);
                other.Group._incompatibleOverlaps.Add(new TextureIncompatibleOverlap(this, other.Compatibility));
            }

            other.Group.SignalIncompatibleOverlapModified();
            SignalIncompatibleOverlapModified();
        }

        /// <summary>
        /// Clear modified flags in the given range.
        /// This will stop any GPU written data from flushing or copying to dependent textures.
        /// </summary>
        /// <param name="range">The range to clear modified flags in</param>
        /// <param name="ignore">Ignore handles that have a copy dependency to the specified group</param>
        public void ClearModified(MultiRange range, TextureGroup ignore = null)
        {
            TextureGroupHandle[] handles = _handles;

            foreach (TextureGroupHandle handle in handles)
            {
                // Handles list is not modified by another thread, only replaced, so this is thread safe.
                // Remove modified flags from all overlapping handles, so that the textures don't flush to unmapped/remapped GPU memory.

                MultiRange subRange = Storage.Range.Slice((ulong)handle.Offset, (ulong)handle.Size);

                if (range.OverlapsWith(subRange))
                {
                    if ((ignore == null || !handle.HasDependencyTo(ignore)) && handle.Modified)
                    {
                        handle.Modified = false;
                        handle.DeferredCopy = null;
                        Storage.SignalModifiedDirty();

                        lock (handle.Overlaps)
                        {
                            foreach (Texture texture in handle.Overlaps)
                            {
                                texture.SignalModifiedDirty();
                            }
                        }
                    }
                }
            }

            Storage.SignalModifiedDirty();

            Texture[] views = _views;

            if (views != null)
            {
                foreach (Texture texture in views)
                {
                    texture.SignalModifiedDirty();
                }
            }
        }

        /// <summary>
        /// A flush has been requested on a tracked region. Flush texture data for the given handle.
        /// </summary>
        /// <param name="handle">The handle this flush action is for</param>
        /// <param name="address">The address of the flushing memory access</param>
        /// <param name="size">The size of the flushing memory access</param>
        public void FlushAction(TextureGroupHandle handle, ulong address, ulong size)
        {
            // There is a small gap here where the action is removed but _actionRegistered is still 1.
            // In this case it will skip registering the action, but here we are already handling it,
            // so there shouldn't be any issue as it's the same handler for all actions.

            handle.ClearActionRegistered();

            if (!handle.Modified)
            {
                return;
            }

            bool isGpuThread = _context.IsGpuThread();

            if (isGpuThread)
            {
                // No need to wait if we're on the GPU thread, we can just clear the modified flag immediately.
                handle.Modified = false;
            }

            _context.Renderer.BackgroundContextAction(() =>
            {
                bool inBuffer = !isGpuThread && handle.Sync(_context);

                Storage.SignalModifiedDirty();

                lock (handle.Overlaps)
                {
                    foreach (Texture texture in handle.Overlaps)
                    {
                        texture.SignalModifiedDirty();
                    }
                }

                if (TextureCompatibility.CanTextureFlush(Storage.Info, _context.Capabilities) && !(inBuffer && _flushBufferImported))
                {
                    FlushSliceRange(false, handle.BaseSlice, handle.BaseSlice + handle.SliceCount, inBuffer, Storage.GetFlushTexture());
                }
            });
        }

        /// <summary>
        /// Called if any part of the storage texture is unmapped.
        /// </summary>
        public void Unmapped()
        {
            if (_flushBufferImported)
            {
                _flushBufferInvalid = true;
            }
        }

        /// <summary>
        /// Dispose this texture group, disposing all related memory tracking handles.
        /// </summary>
        public void Dispose()
        {
            foreach (TextureGroupHandle group in _handles)
            {
                group.Dispose();
            }

            NeoDisposeBands();

            foreach (TextureIncompatibleOverlap incompatible in _incompatibleOverlaps)
            {
                incompatible.Group._incompatibleOverlaps.RemoveAll(overlap => overlap.Group == this);
            }

            if (_flushBuffer != BufferHandle.Null)
            {
                _context.Renderer.DeleteBuffer(_flushBuffer);
            }
        }
    }
}
