using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace FileSystem.Zfs;

/// <summary>
/// The storage pool as its devices describe it: labels, the active uberblock, the
/// top-level vdevs, and reading a block pointer into logical bytes — copies, gangs,
/// embedded data, holes, checksums and compression (<c>docs/ZFS-ON-DISK.md</c>).
/// </summary>
internal sealed class ZfsPool : IDisposable {
  private const int CacheCapacity = 256;
  private const int CacheableSize = 128 * 1024;

  private readonly ImageAccessor[] _devices;
  private readonly Dictionary<uint, ZfsVdev> _topLevel = new();
  private readonly Dictionary<(uint, long, ulong), byte[]> _cache = new();
  private readonly Queue<(uint, long, ulong)> _cacheOrder = new();
  private readonly HashSet<string> _unverified = new(StringComparer.Ordinal);

  public ZfsPool(IReadOnlyList<Stream> devices, bool leaveOpen) {
    if (devices.Count == 0) throw new ArgumentException("At least one device is required.", nameof(devices));
    this._devices = devices.Select(d => new ImageAccessor(d, leaveOpen)).ToArray();
    try {
      this.Open();
    } catch {
      this.Dispose();
      throw;
    }
  }

  public string? Name { get; private set; }
  public ulong Guid { get; private set; }
  public ulong Version { get; private set; }
  public ulong Txg { get; private set; }
  public int VdevChildren { get; private set; }
  public long FirstDeviceLength => this._devices[0].Length;
  public IReadOnlyDictionary<uint, ZfsVdev> TopLevelVdevs => this._topLevel;
  public IReadOnlyCollection<string> UnverifiedChecksums => this._unverified;
  public ZfsObjectSet Mos { get; private set; } = null!;

  /// <summary>What reading has met so far: which block shapes, compressions and checksums.</summary>
  internal ZfsReadStatistics Statistics { get; } = new();

  private void Open() {
    var labels = new List<(int Device, XdrNvList Config, int Ashift)>();
    var uberblocks = new List<(ulong Txg, ulong Timestamp, ulong Version, ZfsBlockPointer Root)>();

    for (var d = 0; d < this._devices.Length; ++d) {
      var device = this._devices[d];
      if (device.Length < 4L * ZfsConstants.LabelSize) continue;
      var aligned = device.Length / ZfsConstants.LabelSize * ZfsConstants.LabelSize;
      for (var l = 0; l < ZfsConstants.LabelCount; ++l) {
        var labelAt = l * (long)ZfsConstants.LabelSize + (l < 2 ? 0 : aligned - 4L * ZfsConstants.LabelSize);
        XdrNvList config;
        try {
          config = XdrNvList.Decode(device.Read(labelAt + ZfsConstants.NvListOffset, ZfsConstants.NvListSize));
        } catch (InvalidDataException) {
          continue;
        }
        var ashift = (int)(config.List("vdev_tree")?.UInt64("ashift") ?? ZfsConstants.SectorShift);
        labels.Add((d, config, ashift));
        ReadRing(device, labelAt, ashift, uberblocks);
      }
    }

    if (labels.Count == 0)
      throw new InvalidDataException("ZFS: no readable vdev label on any device.");

    // The devices of one pool share its guid; the first device decides which pool this is.
    var poolGuid = labels[0].Config.UInt64("pool_guid") ?? 0;
    var own = labels.Where(l => (l.Config.UInt64("pool_guid") ?? 0) == poolGuid).ToList();
    var newest = own.OrderByDescending(l => l.Config.UInt64("txg") ?? 0).First().Config;
    this.Guid = poolGuid;
    this.Name = newest.String("name");
    this.VdevChildren = (int)(newest.UInt64("vdev_children") ?? 1);

    var leaves = new Dictionary<ulong, ImageAccessor>();
    foreach (var (device, config, _) in own)
      if (config.UInt64("guid") is { } guid)
        leaves.TryAdd(guid, this._devices[device]);
    foreach (var (_, config, ashift) in own.OrderByDescending(l => l.Config.UInt64("txg") ?? 0)) {
      var tree = config.List("vdev_tree");
      if (tree?.UInt64("id") is not { } id || this._topLevel.ContainsKey((uint)id)) continue;
      this._topLevel[(uint)id] = ZfsVdev.Build(tree, leaves, ashift);
    }

    if (uberblocks.Count == 0)
      throw new InvalidDataException("ZFS: no valid uberblock in any label.");

    // Newest first; a tree that cannot be read sends the reader one transaction group back.
    Exception? failure = null;
    foreach (var ub in uberblocks.OrderByDescending(u => u.Txg).ThenByDescending(u => u.Timestamp)) {
      try {
        var mos = this.ReadObjectSet(ub.Root);
        if (mos.Type != ZfsConstants.OstMeta)
          throw new InvalidDataException($"ZFS: root object set has type {mos.Type}, not the MOS.");
        this.Mos = mos;
        this.Txg = ub.Txg;
        this.Version = ub.Version;
        return;
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException) {
        failure ??= e;
      }
    }
    throw new InvalidDataException("ZFS: no uberblock leads to a readable MOS: " + failure!.Message, failure);
  }

  private static void ReadRing(ImageAccessor device, long labelAt, int ashift,
      List<(ulong, ulong, ulong, ZfsBlockPointer)> uberblocks) {
    var slotShift = Math.Min(Math.Max(ashift, ZfsConstants.MinUberblockShift), ZfsConstants.MaxUberblockShift);
    var slotSize = 1 << slotShift;
    var ringAt = labelAt + ZfsConstants.UberblockRingOffset;
    var ring = device.Read(ringAt, ZfsConstants.UberblockRingSize);
    for (var at = 0; at + slotSize <= ring.Length; at += slotSize) {
      var slot = ring.AsSpan(at, slotSize);
      var magic = BinaryPrimitives.ReadUInt64LittleEndian(slot);
      if (magic != ZfsConstants.UberblockMagic) {
        if (BinaryPrimitives.ReverseEndianness(magic) == ZfsConstants.UberblockMagic)
          throw new NotSupportedException("ZFS: the pool was written big-endian; only little-endian pools are read.");
        continue;
      }
      if (!ZfsChecksum.VerifyEmbedded(slot, ringAt + at)) continue;
      uberblocks.Add((
        BinaryPrimitives.ReadUInt64LittleEndian(slot[0x10..]),
        BinaryPrimitives.ReadUInt64LittleEndian(slot[0x20..]),
        BinaryPrimitives.ReadUInt64LittleEndian(slot[0x08..]),
        ZfsBlockPointer.Read(slot[0x28..])));
    }
  }

  /// <summary>Reads the object set a block pointer names.</summary>
  public ZfsObjectSet ReadObjectSet(ZfsBlockPointer bp) {
    var block = this.ReadBlock(bp, cache: false);
    if (block.Length < ZfsConstants.ObjsetTypeOffset + 8)
      throw new InvalidDataException("ZFS: object set block too small.");
    var meta = ZfsDnode.Read(block)
      ?? throw new InvalidDataException("ZFS: object set has no meta-dnode.");
    var type = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(ZfsConstants.ObjsetTypeOffset));
    return new ZfsObjectSet(this, meta, type);
  }

  /// <summary>
  /// The logical bytes a block pointer names: zeros for a hole, the payload for an
  /// embedded pointer, otherwise the first copy whose checksum holds, decompressed.
  /// </summary>
  public byte[] ReadBlock(ZfsBlockPointer bp, bool cache = true) {
    if (bp.IsEmbedded) {
      if (bp.EmbeddedType != 0)
        throw new NotSupportedException($"ZFS: embedded block type {bp.EmbeddedType} carries no data.");
      ++this.Statistics.EmbeddedBlocks;
      this.Statistics.Compressions.Add(bp.Compression);
      return ZfsCompression.Decompress(bp.Compression, bp.EmbeddedPayload(), bp.LogicalSize);
    }
    if (bp.IsHole) {
      ++this.Statistics.Holes;
      return new byte[bp.LogicalSize];
    }
    if (bp.IsEncrypted)
      throw new ZfsEncryptedException("ZFS: the block is encrypted (native encryption); no key is available.");
    if (!bp.IsLittleEndian)
      throw new NotSupportedException("ZFS: big-endian blocks are not read.");

    var key = (bp.Dvas[0].Vdev, bp.Dvas[0].Offset, bp.LogicalBirth);
    if (cache && this._cache.TryGetValue(key, out var cached)) return cached;

    var physical = this.ReadPhysical(bp);
    this.Statistics.Compressions.Add(bp.Compression);
    this.Statistics.Checksums.Add(bp.Checksum);
    var logical = ZfsCompression.Decompress(bp.Compression, physical, bp.LogicalSize);
    if (cache && logical.Length <= CacheableSize) {
      if (this._cache.Count >= CacheCapacity) this._cache.Remove(this._cacheOrder.Dequeue());
      this._cache[key] = logical;
      this._cacheOrder.Enqueue(key);
    }
    return logical;
  }

  /// <summary>The physical (still compressed) bytes of the first copy that passes its checksum.</summary>
  internal byte[] ReadPhysical(ZfsBlockPointer bp) {
    Exception? failure = null;
    foreach (var dva in bp.Dvas) {
      if (dva.IsEmpty) continue;
      try {
        var physical = this.ReadDva(dva, bp.PhysicalSize);
        switch (ZfsChecksum.Verify(bp.Checksum, physical, bp.StoredChecksum)) {
          case ZfsChecksumResult.Match:
            if (failure != null) ++this.Statistics.CopiesRecovered;
            return physical;
          case ZfsChecksumResult.NotVerified:
            this._unverified.Add(ChecksumName(bp.Checksum));
            return physical;
          default:
            failure ??= new InvalidDataException(
              $"ZFS: checksum mismatch for the block at vdev {dva.Vdev} offset {dva.Offset}.");
            break;
        }
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException) {
        failure ??= e;
      }
    }
    throw failure ?? new InvalidDataException("ZFS: block pointer has no copies.");
  }

  private byte[] ReadDva(ZfsDva dva, int physicalSize) {
    if (!this._topLevel.TryGetValue(dva.Vdev, out var vdev))
      throw new ZfsDeviceMissingException($"ZFS: top-level vdev {dva.Vdev} is not among the supplied images.");
    if (!dva.Gang) return vdev.Read(dva.Offset, physicalSize);

    // A gang header lists up to three children whose physical bytes, in order, are the block.
    ++this.Statistics.GangBlocks;
    var header = vdev.Read(dva.Offset, ZfsConstants.GangHeaderSize);
    var data = new byte[physicalSize];
    var taken = 0;
    for (var i = 0; i < 3 && taken < physicalSize; ++i) {
      var child = ZfsBlockPointer.Read(header.AsSpan(i * ZfsConstants.BlockPointerSize));
      if (child.IsHole) continue;
      var piece = this.ReadPhysical(child);
      var n = Math.Min(piece.Length, physicalSize - taken);
      piece.AsSpan(0, n).CopyTo(data.AsSpan(taken));
      taken += n;
    }
    if (taken < physicalSize)
      throw new InvalidDataException("ZFS: gang block children are shorter than the block.");
    return data;
  }

  /// <summary>The zeros a hole reads as.</summary>
  public byte[] Hole(int size) {
    ++this.Statistics.Holes;
    return new byte[size];
  }

  private static string ChecksumName(byte id) => id switch {
    ZfsConstants.ChecksumSkein => "skein",
    ZfsConstants.ChecksumEdonR => "edonr",
    ZfsConstants.ChecksumBlake3 => "blake3",
    _ => $"checksum {id}",
  };

  public void Dispose() {
    foreach (var device in this._devices) device?.Dispose();
  }
}

/// <summary>Counters a test can use to prove a fixture exercised a code path.</summary>
internal sealed class ZfsReadStatistics {
  public int GangBlocks;
  public int EmbeddedBlocks;
  public int Holes;
  public int CopiesRecovered;
  public HashSet<byte> Compressions { get; } = [];
  public HashSet<byte> Checksums { get; } = [];
}

/// <summary>An object set: a meta-dnode whose data is an array of dnodes.</summary>
internal sealed class ZfsObjectSet(ZfsPool pool, ZfsDnode meta, ulong type) {
  public ulong Type => type;
  public ZfsPool Pool => pool;

  /// <summary>The dnode of object <paramref name="objectId"/>, or null when the slot is free.</summary>
  public ZfsDnode? Dnode(ulong objectId) {
    var blockSize = (ulong)meta.DataBlockSize;
    if (blockSize == 0) throw new InvalidDataException("ZFS: meta-dnode has no block size.");
    var offset = objectId * ZfsConstants.DnodeSlotSize;
    var blockId = offset / blockSize;
    if (blockId > meta.MaxBlockId) return null;
    var block = this.ReadBlock(meta, blockId);
    return ZfsDnode.Read(block.AsSpan((int)(offset % blockSize)));
  }

  public ZfsDnode RequireDnode(ulong objectId, string what)
    => this.Dnode(objectId) ?? throw new InvalidDataException($"ZFS: {what} (object {objectId}) is free.");

  /// <summary>Logical block <paramref name="blockId"/> of a dnode, through its indirect tree; zeros for holes.</summary>
  public byte[] ReadBlock(ZfsDnode dnode, ulong blockId, bool cacheData = true) {
    if (blockId > dnode.MaxBlockId) return new byte[dnode.DataBlockSize];
    var levels = Math.Max(dnode.Levels, 1);
    var perIndirectShift = dnode.IndirectBlockShift - 7;
    if (levels > 1 && perIndirectShift <= 0)
      throw new InvalidDataException($"ZFS: indirect block shift {dnode.IndirectBlockShift} is invalid.");

    var top = levels == 1 ? blockId : blockId >> (perIndirectShift * (levels - 1));
    if (top >= (ulong)dnode.BlockPointers.Length) return new byte[dnode.DataBlockSize];
    var bp = dnode.BlockPointers[top];
    for (var level = levels - 1; level > 0; --level) {
      if (bp.IsHole) return pool.Hole(dnode.DataBlockSize);
      var indirect = pool.ReadBlock(bp);
      var index = (int)((blockId >> (perIndirectShift * (level - 1))) & ((1UL << perIndirectShift) - 1));
      bp = ZfsBlockPointer.Read(indirect.AsSpan(index * ZfsConstants.BlockPointerSize));
    }
    if (bp.IsHole) return pool.Hole(dnode.DataBlockSize);
    return pool.ReadBlock(bp, cacheData);
  }

  /// <summary>Decodes the ZAP held by <paramref name="dnode"/>.</summary>
  public List<ZfsZapEntry> Zap(ZfsDnode dnode) => ZfsZap.Decode(id => this.ReadBlock(dnode, id), dnode.MaxBlockId);

  public List<ZfsZapEntry> Zap(ulong objectId, string what) => this.Zap(this.RequireDnode(objectId, what));
}

/// <summary>Content protected by ZFS native encryption.</summary>
internal sealed class ZfsEncryptedException(string message) : IOException(message);
