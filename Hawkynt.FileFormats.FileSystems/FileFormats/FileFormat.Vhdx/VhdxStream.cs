#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Vhdx;

/// <summary>
/// Seekable view of the logical disk stored in a VHDX (MS-VHDX v1). Payload BAT
/// entries are translated through the chunk interleaving of payload and
/// sector-bitmap entries.
/// </summary>
/// <remarks>
/// <para>Block states follow MS-VHDX 2.5.1.1: FULLY_PRESENT reads from the file;
/// ZERO reads zeros; NOT_PRESENT, UNDEFINED and UNMAPPED read zeros in a fixed or
/// dynamic file (zero data is one of the behaviours the specification lists for
/// each). In a differencing file NOT_PRESENT defers to the parent and
/// PARTIALLY_PRESENT consults the chunk's sector bitmap sector by sector
/// (MS-VHDX 2.5.1.2), reading the sectors whose bit is clear from the parent.</para>
/// <para>The parent is found through the parent locator (MS-VHDX 2.6.2.6):
/// <c>relative_path</c>, then <c>volume_path</c>, then <c>absolute_win32_path</c>,
/// resolved next to the child, so a differencing disk can only be read when opened
/// from a <see cref="FileStream" />. A parent whose current DataWriteGuid is neither
/// <c>parent_linkage</c> nor <c>parent_linkage2</c> is refused: it was modified
/// after the child was made and its bytes are no longer the child's base.</para>
/// <para>A file whose active header names a log (LogGuid not zero) has metadata
/// updates that must be replayed before the BAT can be trusted (MS-VHDX 2.3); that
/// is refused rather than read stale.</para>
/// </remarks>
public sealed class VhdxStream : Stream {
  private const ulong StateNotPresent = 0;
  private const ulong StateZero = 2;
  private const ulong StateFullyPresent = 6;
  private const ulong StatePartiallyPresent = 7;
  private const ulong SectorBitmapPresent = 6;
  private const long OneMib = 0x100000;
  private const int MaxChainDepth = 16;

  private static readonly Guid BatGuid = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
  private static readonly Guid MetadataGuid = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
  private static readonly Guid FileParametersGuid = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
  private static readonly Guid VirtualDiskSizeGuid = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
  private static readonly Guid LogicalSectorGuid = new("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");
  private static readonly Guid ParentLocatorGuid = new("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");
  private static readonly Guid VhdxParentLocatorType = new("B04AEFB7-D19E-4A81-B789-25B8E9445913");

  private readonly Stream _backing;
  private readonly long _dataLength;
  private readonly bool _leaveOpen;
  private readonly int _blockSize;
  private readonly int _logicalSectorSize;
  private readonly long _batOffset;
  private readonly int _chunkRatio;
  private readonly ulong[] _payloadBatEntries;
  private readonly ulong[] _sectorBitmapEntries;
  private readonly VhdxStream? _parent;
  private readonly List<Stream> _owned;
  private long _position;

  private VhdxStream(
      Stream backing,
      long dataLength,
      int blockSize,
      int logicalSectorSize,
      long batOffset,
      int chunkRatio,
      ulong[] payloadBatEntries,
      ulong[] sectorBitmapEntries,
      bool hasAmbiguousPayloadBlocks,
      VhdxStream? parent,
      List<Stream> owned,
      bool leaveOpen) {
    this._backing = backing;
    this._dataLength = dataLength;
    this._blockSize = blockSize;
    this._logicalSectorSize = logicalSectorSize;
    this._batOffset = batOffset;
    this._chunkRatio = chunkRatio;
    this._payloadBatEntries = payloadBatEntries;
    this._sectorBitmapEntries = sectorBitmapEntries;
    this.HasAmbiguousPayloadBlocks = hasAmbiguousPayloadBlocks;
    this._parent = parent;
    this._owned = owned;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// True when at least one payload BAT entry has a state other than ZERO or
  /// FULLY_PRESENT. Those states do not provide a single canonical byte value
  /// for a standalone rebuild, so maintenance must fail closed instead of
  /// converting them into explicit zero blocks.
  /// </summary>
  public bool HasAmbiguousPayloadBlocks { get; }

  /// <summary>True when this is a differencing disk read through its parent chain.</summary>
  public bool IsDifferencing => this._parent is not null;

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => this._parent is null && this._backing.CanWrite;
  public override long Length => this._dataLength;

  public override long Position {
    get => this._position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    return this.Read(buffer.AsSpan(offset, count));
  }

  public override int Read(Span<byte> buffer) {
    if (this._position >= this._dataLength || buffer.IsEmpty) return 0;
    var total = (int)Math.Min(buffer.Length, this._dataLength - this._position);
    var done = 0;
    while (done < total) {
      var blockIndex = checked((int)(this._position / this._blockSize));
      var blockOffset = checked((int)(this._position % this._blockSize));
      var take = Math.Min(total - done, this._blockSize - blockOffset);
      this.ReadBlock(blockIndex, blockOffset, buffer.Slice(done, take));
      done += take;
      this._position += take;
    }
    return done;
  }

  private void ReadBlock(int blockIndex, int blockOffset, Span<byte> destination) {
    var entry = blockIndex < this._payloadBatEntries.Length ? this._payloadBatEntries[blockIndex] : StateZero;
    switch (entry & 0x07) {
      case StateFullyPresent:
        this.ReadPhysical(entry, blockOffset, destination);
        return;
      case StatePartiallyPresent:
        if (this._parent is null)
          throw new InvalidDataException(
            $"VHDX: payload block {blockIndex} is PARTIALLY_PRESENT in a file without a parent, which MS-VHDX forbids.");
        this.ReadPartial(blockIndex, blockOffset, entry, destination);
        return;
      case StateNotPresent when this._parent is not null:
        this.ReadParent((long)blockIndex * this._blockSize + blockOffset, destination);
        return;
      default:
        // ZERO is zero by definition. NOT_PRESENT (standalone), UNDEFINED and UNMAPPED
        // may legitimately read as zeros (MS-VHDX 2.5.1.1).
        destination.Clear();
        return;
    }
  }

  private void ReadPhysical(ulong entry, int blockOffset, Span<byte> destination) {
    var physical = checked((long)(entry >> 20) * OneMib + blockOffset);
    if (physical < OneMib || physical + destination.Length > this._backing.Length)
      throw new InvalidDataException($"VHDX: payload block data at offset {physical} lies outside the file.");
    this._backing.Position = physical;
    this._backing.ReadExactly(destination);
  }

  private void ReadParent(long virtualOffset, Span<byte> destination) {
    destination.Clear();
    var parent = this._parent!;
    if (virtualOffset >= parent.Length) return;
    var take = (int)Math.Min(destination.Length, parent.Length - virtualOffset);
    parent.Position = virtualOffset;
    parent.ReadExactly(destination[..take]);
  }

  /// <summary>
  /// PARTIALLY_PRESENT: one bit per logical sector in the chunk's sector bitmap block
  /// (bit set = the sector is in this file), sectors numbered from the chunk's start.
  /// </summary>
  private void ReadPartial(int blockIndex, int blockOffset, ulong entry, Span<byte> destination) {
    var chunk = blockIndex / this._chunkRatio;
    var bitmapEntry = chunk < this._sectorBitmapEntries.Length ? this._sectorBitmapEntries[chunk] : 0;
    if ((bitmapEntry & 0x07) != SectorBitmapPresent)
      throw new InvalidDataException($"VHDX: payload block {blockIndex} is PARTIALLY_PRESENT but its sector bitmap block is not present.");
    var bitmapOffset = checked((long)(bitmapEntry >> 20) * OneMib);
    var sectorsPerBlock = this._blockSize / this._logicalSectorSize;
    var firstSectorInChunk = (long)(blockIndex % this._chunkRatio) * sectorsPerBlock;

    var done = 0;
    while (done < destination.Length) {
      var offsetInBlock = blockOffset + done;
      var sector = offsetInBlock / this._logicalSectorSize;
      // Coalesce a run of sectors that come from the same place.
      var present = this.SectorPresent(bitmapOffset, firstSectorInChunk + sector);
      var runEnd = sector + 1;
      while ((long)runEnd * this._logicalSectorSize < blockOffset + destination.Length
             && this.SectorPresent(bitmapOffset, firstSectorInChunk + runEnd) == present)
        runEnd++;
      var take = (int)Math.Min(destination.Length - done, (long)runEnd * this._logicalSectorSize - offsetInBlock);
      var slice = destination.Slice(done, take);
      if (present)
        this.ReadPhysical(entry, offsetInBlock, slice);
      else
        this.ReadParent((long)blockIndex * this._blockSize + offsetInBlock, slice);
      done += take;
    }
  }

  private long _bitmapCacheOffset = -1;
  private readonly byte[] _bitmapCache = new byte[4096];

  private bool SectorPresent(long bitmapOffset, long sectorInChunk) {
    var byteIndex = bitmapOffset + sectorInChunk / 8;
    var page = byteIndex & ~4095L;
    if (page != this._bitmapCacheOffset) {
      if (page + 4096 > this._backing.Length)
        throw new InvalidDataException("VHDX: sector bitmap block lies outside the file.");
      this._backing.Position = page;
      this._backing.ReadExactly(this._bitmapCache);
      this._bitmapCacheOffset = page;
    }
    return (this._bitmapCache[byteIndex - page] >> (int)(sectorInChunk % 8) & 1) != 0;
  }

  public override void Write(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (!this.CanWrite) throw new NotSupportedException(this._parent is null
      ? "Backing stream is not writable."
      : "Writing to a differencing VHDX is not supported.");
    if (this._position + count > this._dataLength)
      throw new InvalidOperationException(
        $"Write would exceed virtual disk size ({this._dataLength} bytes). Position={this._position}, Count={count}.");

    var remaining = count;
    while (remaining > 0) {
      var blockIndex = checked((int)(this._position / this._blockSize));
      var blockOffset = checked((int)(this._position % this._blockSize));
      var take = Math.Min(remaining, this._blockSize - blockOffset);
      if (blockIndex >= this._payloadBatEntries.Length)
        throw new InvalidOperationException($"Block index {blockIndex} is outside the VHDX BAT.");

      var entry = this._payloadBatEntries[blockIndex];
      if ((entry & 0x07) != StateFullyPresent) {
        this.AllocateBlock(blockIndex);
        entry = this._payloadBatEntries[blockIndex];
      }

      var fileOffsetMib = entry >> 20;
      var physicalOffset = checked((long)fileOffsetMib * OneMib + blockOffset);
      this._backing.Position = physicalOffset;
      this._backing.Write(buffer, offset, take);

      offset += take;
      remaining -= take;
      this._position += take;
    }
  }

  public override void Write(ReadOnlySpan<byte> buffer) {
    if (buffer.Length == 0) return;
    var temp = buffer.ToArray();
    this.Write(temp, 0, temp.Length);
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var next = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._dataLength + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (next < 0) throw new IOException("Seek before beginning of stream.");
    this._position = next;
    return next;
  }

  public override void SetLength(long value) {
    if (value != this._dataLength)
      throw new NotSupportedException(
        $"Cannot change a VHDX guest disk length (current={this._dataLength}, requested={value}).");
  }

  public override void Flush() {
    if (this._backing.CanWrite) this._backing.Flush();
  }

  protected override void Dispose(bool disposing) {
    if (disposing) {
      this._parent?.Dispose();
      foreach (var s in this._owned) s.Dispose();
      if (!this._leaveOpen) this._backing.Dispose();
    }
    base.Dispose(disposing);
  }

  private void AllocateBlock(int blockIndex) {
    var aligned = checked((this._backing.Length + OneMib - 1) / OneMib * OneMib);
    this._backing.SetLength(checked(aligned + this._blockSize));

    this._backing.Position = aligned;
    var zeros = new byte[Math.Min(this._blockSize, 64 * 1024)];
    var remaining = this._blockSize;
    while (remaining > 0) {
      var take = Math.Min(remaining, zeros.Length);
      this._backing.Write(zeros, 0, take);
      remaining -= take;
    }

    var entry = checked(((ulong)(aligned / OneMib) << 20) | StateFullyPresent);
    this._payloadBatEntries[blockIndex] = entry;
    var rawBatIndex = VhdxWriter.PayloadBatIndex(blockIndex, this._chunkRatio);
    this._backing.Position = checked(this._batOffset + rawBatIndex * 8L);
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, entry);
    this._backing.Write(bytes);
  }

  // ── Opening ───────────────────────────────────────────────────────

  /// <summary>
  /// Opens a standalone (fixed or dynamic) VHDX guest disk for reading and in-place
  /// editing. Returns <c>null</c> for anything else, including differencing disks and
  /// files with a log awaiting replay; use <see cref="OpenReadOnly" /> to read those.
  /// </summary>
  public static VhdxStream? TryOpen(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    try {
      return Open(stream, allowParent: false, 0, out _);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException
                                   or UnauthorizedAccessException or OverflowException or ArgumentException) {
      if (stream.CanSeek) stream.Position = 0;
      return null;
    }
  }

  /// <summary>
  /// Opens any VHDX guest disk for reading, following a differencing disk's parent
  /// chain. Returns <c>null</c> with the reason when the disk cannot be read faithfully.
  /// </summary>
  public static VhdxStream? OpenReadOnly(Stream stream, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    try {
      var result = Open(stream, allowParent: true, 0, out reason);
      if (result is null && stream.CanSeek) stream.Position = 0;
      return result;
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException
                                   or UnauthorizedAccessException or OverflowException or ArgumentException) {
      reason = ex.Message;
      if (stream.CanSeek) stream.Position = 0;
      return null;
    }
  }

  private static VhdxStream? Open(Stream stream, bool allowParent, int depth, out string? reason) {
    reason = null;
    if (!stream.CanSeek || stream.Length < 0x110000) { reason = "not a VHDX (too short)"; return null; }
    stream.Position = 0;
    Span<byte> magic = stackalloc byte[8];
    stream.ReadExactly(magic);
    if (!magic.SequenceEqual("vhdxfile"u8)) { reason = "not a VHDX (no file type identifier)"; return null; }

    var header = ReadActiveHeader(stream)
      ?? throw new InvalidDataException("VHDX: neither header has a valid signature and checksum.");
    if (header.LogGuid != Guid.Empty) {
      reason = "VHDX: the log holds metadata updates that were never replayed (LogGuid set); refusing to read stale data.";
      return null;
    }

    if (!TryReadRegions(stream, out var batOffset, out var batLength, out var metadataOffset))
      throw new InvalidDataException("VHDX: region table lacks the BAT or metadata region.");
    var meta = ReadMetadata(stream, metadataOffset);
    if (meta.BlockSize <= 0 || meta.VirtualDiskSize <= 0 || meta.LogicalSectorSize <= 0
        || meta.BlockSize % meta.LogicalSectorSize != 0)
      throw new InvalidDataException("VHDX: required metadata items are missing or inconsistent.");

    VhdxStream? parent = null;
    var owned = new List<Stream>();
    if (meta.HasParent) {
      if (!allowParent) { reason = "VHDX: differencing disk"; return null; }
      if (depth >= MaxChainDepth)
        throw new InvalidDataException("VHDX: parent chain is too deep (or circular).");
      parent = OpenParent(stream, meta, depth, owned);
    }

    try {
      var chunkBytes = checked((1L << 23) * meta.LogicalSectorSize);
      if (chunkBytes % meta.BlockSize != 0)
        throw new InvalidDataException("VHDX: block size does not divide the chunk size.");
      var chunkRatioLong = chunkBytes / meta.BlockSize;
      if (chunkRatioLong is <= 0 or > int.MaxValue)
        throw new InvalidDataException("VHDX: invalid chunk ratio.");
      var chunkRatio = (int)chunkRatioLong;

      var payloadBlockCountLong = (meta.VirtualDiskSize + meta.BlockSize - 1) / meta.BlockSize;
      if (payloadBlockCountLong is <= 0 or > int.MaxValue)
        throw new InvalidDataException("VHDX: invalid payload block count.");
      var payloadBlockCount = (int)payloadBlockCountLong;
      var chunkCount = (payloadBlockCount + chunkRatio - 1) / chunkRatio;
      // Payload entries interleave with one sector-bitmap entry per chunk; a
      // differencing file has the trailing bitmap entry for a partial last chunk too.
      var rawBatEntries = meta.HasParent
        ? (long)chunkCount * (chunkRatio + 1)
        : payloadBlockCount + (payloadBlockCount - 1) / chunkRatio;
      if (rawBatEntries * 8 > batLength)
        throw new InvalidDataException("VHDX: BAT region is too small for the disk size.");

      var payloadEntries = new ulong[payloadBlockCount];
      var bitmapEntries = new ulong[meta.HasParent ? chunkCount : 0];
      var hasAmbiguousPayloadBlocks = false;
      var bat = new byte[checked((int)(rawBatEntries * 8))];
      stream.Position = batOffset;
      stream.ReadExactly(bat);
      for (var blockIndex = 0; blockIndex < payloadBlockCount; ++blockIndex) {
        var entry = BinaryPrimitives.ReadUInt64LittleEndian(bat.AsSpan(VhdxWriter.PayloadBatIndex(blockIndex, chunkRatio) * 8));
        payloadEntries[blockIndex] = entry;
        if ((entry & 0x07) is not (StateZero or StateFullyPresent))
          hasAmbiguousPayloadBlocks = true;
      }
      for (var chunk = 0; chunk < bitmapEntries.Length; ++chunk)
        bitmapEntries[chunk] = BinaryPrimitives.ReadUInt64LittleEndian(bat.AsSpan(checked(((chunk + 1) * (chunkRatio + 1) - 1) * 8)));

      stream.Position = 0;
      return new VhdxStream(stream, meta.VirtualDiskSize, meta.BlockSize, meta.LogicalSectorSize, batOffset, chunkRatio,
        payloadEntries, bitmapEntries, hasAmbiguousPayloadBlocks, parent, owned, leaveOpen: true);
    } catch {
      parent?.Dispose();
      foreach (var s in owned) s.Dispose();
      throw;
    }
  }

  private static VhdxStream OpenParent(Stream child, Metadata meta, int depth, List<Stream> owned) {
    var locator = meta.ParentLocator
      ?? throw new InvalidDataException("VHDX: differencing disk has no parent locator.");
    if (child is not FileStream fs)
      throw new NotSupportedException("VHDX: a differencing disk needs its parent file; open it from a file path so the parent can be found.");
    var childDir = Path.GetDirectoryName(Path.GetFullPath(fs.Name)) ?? ".";

    string? found = null;
    foreach (var key in new[] { "relative_path", "volume_path", "absolute_win32_path" }) {
      if (!locator.TryGetValue(key, out var value) || value.Length == 0) continue;
      var candidate = ResolveLocatorPath(childDir, key, value);
      if (candidate is not null && File.Exists(candidate)) { found = candidate; break; }
    }
    if (found is null) {
      locator.TryGetValue("relative_path", out var rel);
      locator.TryGetValue("absolute_win32_path", out var abs);
      throw new InvalidDataException($"VHDX: parent disk not found (relative_path '{rel}', absolute_win32_path '{abs}').");
    }

    var file = new FileStream(found, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    owned.Add(file);
    var parentHeader = ReadActiveHeader(file)
      ?? throw new InvalidDataException($"VHDX: parent '{found}' has no valid header.");
    var linkages = new[] { "parent_linkage", "parent_linkage2" }
      .Select(k => locator.TryGetValue(k, out var v) && Guid.TryParse(v, out var g) ? g : (Guid?)null)
      .Where(g => g is not null).ToArray();
    if (linkages.Length == 0)
      throw new InvalidDataException("VHDX: parent locator has no parent_linkage.");
    if (!linkages.Contains(parentHeader.DataWriteGuid))
      throw new InvalidDataException(
        $"VHDX: parent '{Path.GetFileName(found)}' has DataWriteGuid {parentHeader.DataWriteGuid}, the child expects {linkages[0]}; the parent changed after the child was made.");
    return Open(file, allowParent: true, depth + 1, out var reason)
      ?? throw new InvalidDataException($"VHDX: parent '{Path.GetFileName(found)}' cannot be read: {reason}");
  }

  private static string? ResolveLocatorPath(string childDir, string key, string value) {
    var normalized = value.Replace('\\', Path.DirectorySeparatorChar);
    switch (key) {
      case "relative_path":
        return Path.GetFullPath(Path.Combine(childDir, normalized));
      case "absolute_win32_path":
        if (OperatingSystem.IsWindows()) return value;
        // Off Windows only the file name can still be meaningful: look beside the child.
        return Path.Combine(childDir, Path.GetFileName(normalized));
      default:
        // \\?\Volume{GUID}\path only resolves on the machine that wrote it.
        return OperatingSystem.IsWindows() ? value : null;
    }
  }

  private sealed record Header(ulong Sequence, Guid DataWriteGuid, Guid LogGuid);

  /// <summary>The header with the valid checksum and the higher sequence number (MS-VHDX 2.2.2).</summary>
  private static Header? ReadActiveHeader(Stream stream) {
    Header? best = null;
    var buffer = new byte[4096];
    foreach (var offset in new[] { 0x10000L, 0x20000L }) {
      if (offset + 4096 > stream.Length) continue;
      stream.Position = offset;
      stream.ReadExactly(buffer);
      if (!buffer.AsSpan(0, 4).SequenceEqual("head"u8)) continue;
      var stored = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0);
      if (Crc32.Compute(buffer, Crc32.Castagnoli) != stored) continue;
      var header = new Header(
        BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8)),
        new Guid(buffer.AsSpan(32, 16)),
        new Guid(buffer.AsSpan(48, 16)));
      if (best is null || header.Sequence > best.Sequence) best = header;
    }
    return best;
  }

  private static bool TryReadRegions(Stream stream, out long batOffset, out long batLength, out long metadataOffset) {
    batOffset = 0;
    batLength = 0;
    metadataOffset = 0;
    stream.Position = 0x30000;
    Span<byte> header = stackalloc byte[16];
    stream.ReadExactly(header);
    if (!header[..4].SequenceEqual("regi"u8)) return false;
    var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    if (count > 2047) return false;

    Span<byte> entry = stackalloc byte[32];
    for (var i = 0u; i < count; ++i) {
      stream.Position = 0x30000 + 16 + i * 32L;
      stream.ReadExactly(entry);
      var guid = new Guid(entry[..16]);
      var offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..24]));
      var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..28]);
      if (guid == BatGuid) {
        batOffset = offset;
        batLength = length;
      } else if (guid == MetadataGuid) {
        metadataOffset = offset;
      }
    }
    return batOffset > 0 && batLength > 0 && metadataOffset > 0;
  }

  private sealed record Metadata(int BlockSize, long VirtualDiskSize, int LogicalSectorSize, bool HasParent,
    Dictionary<string, string>? ParentLocator);

  private static Metadata ReadMetadata(Stream stream, long metadataOffset) {
    int blockSize = 0, logicalSectorSize = 0;
    long virtualDiskSize = 0;
    var hasParent = false;
    Dictionary<string, string>? locator = null;

    stream.Position = metadataOffset;
    Span<byte> header = stackalloc byte[12];
    stream.ReadExactly(header);
    if (!header[..8].SequenceEqual("metadata"u8))
      throw new InvalidDataException("VHDX: metadata region has no signature.");
    var count = BinaryPrimitives.ReadUInt16LittleEndian(header[10..12]);

    Span<byte> entry = stackalloc byte[32];
    for (var i = 0; i < count; ++i) {
      stream.Position = metadataOffset + 32 + i * 32L;
      stream.ReadExactly(entry);
      var guid = new Guid(entry[..16]);
      var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..20]);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..24]);
      if (relativeOffset == 0 || length == 0) continue;
      var valueOffset = checked(metadataOffset + relativeOffset);
      if (valueOffset < 0 || valueOffset + length > stream.Length || length > 1 << 20)
        throw new InvalidDataException("VHDX: metadata item lies outside the file.");
      var value = new byte[length];
      stream.Position = valueOffset;
      stream.ReadExactly(value);

      if (guid == FileParametersGuid && length >= 8) {
        blockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value));
        hasParent = (BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4)) & 0x02) != 0;
      } else if (guid == VirtualDiskSizeGuid && length >= 8) {
        virtualDiskSize = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(value));
      } else if (guid == LogicalSectorGuid && length >= 4) {
        logicalSectorSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value));
      } else if (guid == ParentLocatorGuid) {
        locator = ParseParentLocator(value);
      }
    }
    return new Metadata(blockSize, virtualDiskSize, logicalSectorSize, hasParent, locator);
  }

  /// <summary>
  /// Parent locator (MS-VHDX 2.6.2.6): LocatorType GUID, reserved u16, KeyValueCount
  /// u16, then {KeyOffset u32, ValueOffset u32, KeyLength u16, ValueLength u16}
  /// entries pointing at UTF-16LE strings, offsets from the item's start.
  /// </summary>
  private static Dictionary<string, string> ParseParentLocator(byte[] item) {
    if (item.Length < 20)
      throw new InvalidDataException("VHDX: parent locator is truncated.");
    if (new Guid(item.AsSpan(0, 16)) != VhdxParentLocatorType)
      throw new NotSupportedException("VHDX: parent locator type is not the VHDX locator.");
    var count = BinaryPrimitives.ReadUInt16LittleEndian(item.AsSpan(18));
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < count; i++) {
      var at = 20 + i * 12;
      if (at + 12 > item.Length) throw new InvalidDataException("VHDX: parent locator entry is truncated.");
      var keyOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(item.AsSpan(at));
      var valueOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(item.AsSpan(at + 4));
      var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(item.AsSpan(at + 8));
      var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(item.AsSpan(at + 10));
      if (keyOffset < 0 || keyOffset + keyLength > item.Length || valueOffset < 0 || valueOffset + valueLength > item.Length)
        throw new InvalidDataException("VHDX: parent locator string lies outside the item.");
      var key = Encoding.Unicode.GetString(item, keyOffset, keyLength);
      var value = Encoding.Unicode.GetString(item, valueOffset, valueLength);
      result[key] = value;
    }
    return result;
  }
}
