#pragma warning disable CS1591

namespace FileFormat.Qcow2;

/// <summary>
/// Seekable access to the active guest disk of a QCOW2 image.
/// Reads resolve standard, zero, unallocated and compressed clusters (deflate or zstd),
/// extended L2 entries with their 32 subclusters, and backing files (QCOW2 or raw), which
/// supply every cluster or subcluster the image leaves unallocated.
/// Writes are enabled only for flat standard-refcount profiles and honour QCOW2
/// copy-on-write semantics for shared L2 and data clusters.
/// </summary>
/// <remarks>
/// Layout per QEMU's <c>docs/interop/qcow2.txt</c>. A backing file is found the way QEMU finds
/// it: its name as stored, relative to the directory of the image, so the image must be opened
/// from a <see cref="FileStream"/>; its format comes from the backing-format header extension,
/// or from its magic when that is missing.
/// </remarks>
public sealed class Qcow2Stream : Stream {
  private const int MaxBackingDepth = 16;

  private readonly Stream _backing;
  private readonly bool _leaveOpen;
  private readonly Qcow2Header _header;
  private readonly int _clusterSize;
  private readonly int _l2Entries;
  private readonly bool _writableProfile;
  private readonly Stream? _parent;
  private long _position;

  private Qcow2Stream(Stream backing, Qcow2Header header, bool leaveOpen, Stream? parent) {
    _backing = backing;
    _header = header;
    _clusterSize = header.ClusterSize;
    _l2Entries = header.L2Entries;
    _writableProfile = backing.CanWrite && Qcow2Structures.IsWritableProfile(header);
    _leaveOpen = leaveOpen;
    _parent = parent;
  }

  /// <summary>Attempts to open a supported QCOW2 image as a virtual guest-disk stream.</summary>
  public static Qcow2Stream? TryOpen(Stream backing, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(backing);
    if (!backing.CanRead || !backing.CanSeek)
      return null;
    try {
      return Open(backing, leaveOpen);
    } catch (InvalidDataException) {
      return null;
    } catch (NotSupportedException) {
      return null;
    } catch (IOException) {
      return null;
    }
  }

  /// <summary>Opens a QCOW2 image as a virtual guest-disk stream, or explains why it cannot.</summary>
  /// <exception cref="InvalidDataException">The image or its backing chain is malformed or missing.</exception>
  /// <exception cref="NotSupportedException">The image uses a feature this reader does not decode.</exception>
  public static Qcow2Stream Open(Stream backing, bool leaveOpen = true) => Open(backing, leaveOpen, depth: 0);

  private static Qcow2Stream Open(Stream backing, bool leaveOpen, int depth) {
    ArgumentNullException.ThrowIfNull(backing);
    var header = Qcow2Structures.ReadHeader(backing);
    Qcow2Structures.ValidateReadableProfile(header);
    var parent = header.HasBackingFile ? OpenBackingFile(backing, header, depth) : null;
    return new Qcow2Stream(backing, header, leaveOpen, parent);
  }

  /// <summary>Opens the image's backing file, QCOW2 (recursively) or raw.</summary>
  private static Stream OpenBackingFile(Stream image, in Qcow2Header header, int depth) {
    if (depth >= MaxBackingDepth)
      throw new InvalidDataException($"QCOW2: backing chain is deeper than {MaxBackingDepth} images.");
    if (header.BackingFileSize > 1023 || header.BackingFileOffset > (ulong)(image.Length - header.BackingFileSize))
      throw new InvalidDataException("QCOW2: backing file name lies outside the image.");
    var nameBytes = new byte[header.BackingFileSize];
    Qcow2Structures.ReadExactlyAt(image, (long)header.BackingFileOffset, nameBytes);
    var name = System.Text.Encoding.UTF8.GetString(nameBytes);
    if (name.Contains(':') && !Path.IsPathRooted(name))
      throw new NotSupportedException($"QCOW2: backing file '{name}' is a protocol URL, not a file.");
    if (image is not FileStream file)
      throw new NotSupportedException($"QCOW2: the image has the backing file '{name}'; open it from a file path so that file can be found.");

    var directory = Path.GetDirectoryName(Path.GetFullPath(file.Name)) ?? ".";
    var path = Path.IsPathRooted(name) ? name : Path.Combine(directory, name);
    if (!File.Exists(path))
      throw new InvalidDataException($"QCOW2: backing file '{name}' was not found next to the image.");

    var parentFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    try {
      var format = header.BackingFormat ?? (StartsWithMagic(parentFile) ? "qcow2" : "raw");
      return format switch {
        "qcow2" => Open(parentFile, leaveOpen: false, depth + 1),
        "raw" => parentFile,
        _ => throw new NotSupportedException($"QCOW2: backing file format '{format}' is not supported."),
      };
    } catch {
      parentFile.Dispose();
      throw;
    }
  }

  private static bool StartsWithMagic(Stream stream) {
    Span<byte> magic = stackalloc byte[4];
    stream.Position = 0;
    return stream.Read(magic) == 4 && magic.SequenceEqual(Qcow2Structures.Magic);
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => _writableProfile;
  public override long Length => _header.VirtualSize;

  public override long Position {
    get => _position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      _position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (offset < 0 || count < 0 || offset > buffer.Length - count)
      throw new ArgumentOutOfRangeException(nameof(offset));
    if (_position >= Length || count == 0)
      return 0;

    var remaining = checked((int)Math.Min(count, Length - _position));
    var totalRead = 0;
    while (remaining > 0) {
      var guestClusterIndex = _position / _clusterSize;
      var inClusterOffset = (int)(_position % _clusterSize);
      var chunk = Math.Min(remaining, _clusterSize - inClusterOffset);
      ReadGuestRange(guestClusterIndex, inClusterOffset, buffer.AsSpan(offset + totalRead, chunk));
      _position += chunk;
      totalRead += chunk;
      remaining -= chunk;
    }
    return totalRead;
  }

  public override int Read(Span<byte> buffer) {
    if (_position >= Length || buffer.IsEmpty)
      return 0;
    var remaining = checked((int)Math.Min(buffer.Length, Length - _position));
    var totalRead = 0;
    while (remaining > 0) {
      var guestClusterIndex = _position / _clusterSize;
      var inClusterOffset = (int)(_position % _clusterSize);
      var chunk = Math.Min(remaining, _clusterSize - inClusterOffset);
      ReadGuestRange(guestClusterIndex, inClusterOffset, buffer.Slice(totalRead, chunk));
      _position += chunk;
      totalRead += chunk;
      remaining -= chunk;
    }
    return totalRead;
  }

  public override void Write(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (offset < 0 || count < 0 || offset > buffer.Length - count)
      throw new ArgumentOutOfRangeException(nameof(offset));
    Write(buffer.AsSpan(offset, count));
  }

  public override void Write(ReadOnlySpan<byte> buffer) {
    if (!CanWrite)
      throw new NotSupportedException(
        "QCOW2 write access requires a flat image with 16-bit eager refcounts, no snapshots, " +
        "no backing file, no encryption, and no incompatible or autoclear v3 features.");
    if (_position < 0 || _position > Length - buffer.Length)
      throw new EndOfStreamException("Write would extend beyond the fixed QCOW2 virtual disk size.");

    var remaining = buffer.Length;
    var consumed = 0;
    while (remaining > 0) {
      var guestClusterIndex = _position / _clusterSize;
      var inClusterOffset = (int)(_position % _clusterSize);
      var chunk = Math.Min(remaining, _clusterSize - inClusterOffset);
      WriteGuestRange(guestClusterIndex, inClusterOffset, buffer.Slice(consumed, chunk));
      _position += chunk;
      consumed += chunk;
      remaining -= chunk;
    }
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var next = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => checked(_position + offset),
      SeekOrigin.End => checked(Length + offset),
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (next < 0)
      throw new IOException("Attempted to seek before the start of the guest disk.");
    _position = next;
    return next;
  }

  public override void SetLength(long value) {
    if (value != Length)
      throw new NotSupportedException("QCOW2 virtual disk resizing is not implemented by this stream.");
  }

  public override void Flush() => _backing.Flush();

  protected override void Dispose(bool disposing) {
    if (disposing) {
      _parent?.Dispose();
      if (!_leaveOpen)
        _backing.Dispose();
    }
    base.Dispose(disposing);
  }

  private void ReadGuestRange(long guestClusterIndex, int inClusterOffset, Span<byte> destination) {
    destination.Clear();
    var guestOffset = checked(guestClusterIndex * _clusterSize + inClusterOffset);
    var entry = ReadL2Entry(guestClusterIndex, out _, out var bitmap);

    if ((entry & Qcow2Structures.CompressedFlag) != 0) {
      var cluster = new byte[_clusterSize];
      Qcow2Structures.InflateCompressedCluster(_backing, entry, _header.ClusterBits, _header.CompressionType, cluster);
      cluster.AsSpan(inClusterOffset, destination.Length).CopyTo(destination);
      return;
    }

    if (entry != 0)
      ValidateStandardL2Entry(entry);
    var hostOffset = Qcow2Structures.ReadClusterOffset(entry);

    if (_header.ExtendedL2) {
      // Each of the 32 subclusters is allocated (bit n), reads as zeros (bit 32 + n), or
      // unallocated (neither), in which case the backing file supplies it.
      var subclusterSize = _clusterSize / Qcow2Structures.Subclusters;
      var allocated = (uint)bitmap;
      var zero = (uint)(bitmap >> 32);
      if ((allocated & zero) != 0)
        throw new InvalidDataException("QCOW2: a subcluster is marked both allocated and zero.");
      if (allocated != 0 && hostOffset == 0)
        throw new InvalidDataException("QCOW2: allocated subclusters without a host cluster.");
      var done = 0;
      while (done < destination.Length) {
        var inCluster = inClusterOffset + done;
        var subcluster = inCluster / subclusterSize;
        var length = Math.Min(destination.Length - done, (subcluster + 1) * subclusterSize - inCluster);
        var part = destination.Slice(done, length);
        if ((allocated & (1u << subcluster)) != 0)
          Qcow2Structures.ReadExactlyAt(_backing, checked(hostOffset + inCluster), part);
        else if ((zero & (1u << subcluster)) == 0)
          this.ReadFromParent(checked(guestOffset + done), part);
        done += length;
      }
      return;
    }

    if (_header.Version >= 3 && (entry & Qcow2Structures.ZeroFlag) != 0)
      return;
    if (hostOffset == 0) {
      if ((entry & Qcow2Structures.CopiedFlag) != 0)
        throw new InvalidDataException("QCOW2: self-contained standard cluster has copied flag but zero host offset.");
      this.ReadFromParent(guestOffset, destination);
      return;
    }

    Qcow2Structures.ReadExactlyAt(_backing, checked(hostOffset + inClusterOffset), destination);
  }

  /// <summary>Fills <paramref name="destination"/> from the backing file; zeros where it is shorter or absent.</summary>
  private void ReadFromParent(long guestOffset, Span<byte> destination) {
    if (_parent == null || guestOffset >= _parent.Length)
      return;
    var available = (int)Math.Min(destination.Length, _parent.Length - guestOffset);
    _parent.Position = guestOffset;
    _parent.ReadExactly(destination[..available]);
  }

  private void WriteGuestRange(long guestClusterIndex, int inClusterOffset, ReadOnlySpan<byte> source) {
    var l2TableOffset = EnsureExclusiveL2Table(guestClusterIndex);
    var l2Index = checked((int)(guestClusterIndex % _l2Entries));
    var l2EntryOffset = checked(l2TableOffset + l2Index * 8L);
    var oldEntry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l2EntryOffset);

    if ((oldEntry & Qcow2Structures.CompressedFlag) == 0) {
      ValidateStandardL2Entry(oldEntry);
      var oldHostOffset = Qcow2Structures.ReadClusterOffset(oldEntry);
      if (oldHostOffset != 0) {
        var refcount = GetRefcount(oldHostOffset);
        if (refcount == 0)
          throw new InvalidDataException("QCOW2: allocated guest cluster has a zero refcount.");
        if (refcount == 1) {
          if (_header.Version >= 3 && (oldEntry & Qcow2Structures.ZeroFlag) != 0) {
            ZeroCluster(oldHostOffset);
            Qcow2Structures.WriteUInt64BigEndianAt(
              _backing,
              l2EntryOffset,
              (ulong)oldHostOffset | Qcow2Structures.CopiedFlag);
          } else if ((oldEntry & Qcow2Structures.CopiedFlag) == 0) {
            Qcow2Structures.WriteUInt64BigEndianAt(
              _backing,
              l2EntryOffset,
              oldEntry | Qcow2Structures.CopiedFlag);
          }
          _backing.Position = checked(oldHostOffset + inClusterOffset);
          _backing.Write(source);
          return;
        }
      } else if ((oldEntry & Qcow2Structures.CopiedFlag) != 0) {
        throw new InvalidDataException("QCOW2: self-contained standard cluster has copied flag but zero host offset.");
      }
    }

    var replacement = new byte[_clusterSize];
    if (inClusterOffset != 0 || source.Length != _clusterSize)
      ReadGuestRange(guestClusterIndex, 0, replacement);
    source.CopyTo(replacement.AsSpan(inClusterOffset));

    var newHostOffset = AllocateRefcountedCluster(clear: false);
    _backing.Position = newHostOffset;
    _backing.Write(replacement);
    Qcow2Structures.WriteUInt64BigEndianAt(
      _backing,
      l2EntryOffset,
      (ulong)newHostOffset | Qcow2Structures.CopiedFlag);

    ReleaseEntryStorage(oldEntry);
  }

  private long EnsureExclusiveL2Table(long guestClusterIndex) {
    var l1Index = guestClusterIndex / _l2Entries;
    if (l1Index < 0 || l1Index >= _header.L1Size)
      throw new EndOfStreamException("Guest cluster lies outside the QCOW2 L1 address space.");

    var l1EntryOffset = checked(_header.L1TableOffset + l1Index * 8L);
    var l1Entry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l1EntryOffset);
    ValidateL1Entry(l1Entry);
    var l2TableOffset = Qcow2Structures.ReadClusterOffset(l1Entry);

    if (l2TableOffset == 0) {
      l2TableOffset = AllocateRefcountedCluster(clear: true);
      Qcow2Structures.WriteUInt64BigEndianAt(
        _backing,
        l1EntryOffset,
        (ulong)l2TableOffset | Qcow2Structures.CopiedFlag);
      return l2TableOffset;
    }

    var refcount = GetRefcount(l2TableOffset);
    if (refcount == 0)
      throw new InvalidDataException("QCOW2: active L2 table has a zero refcount.");
    if (refcount == 1) {
      if ((l1Entry & Qcow2Structures.CopiedFlag) == 0)
        Qcow2Structures.WriteUInt64BigEndianAt(
          _backing,
          l1EntryOffset,
          l1Entry | Qcow2Structures.CopiedFlag);
      return l2TableOffset;
    }

    var replacementOffset = AllocateRefcountedCluster(clear: false);
    var replacement = new byte[_clusterSize];
    Qcow2Structures.ReadExactlyAt(_backing, l2TableOffset, replacement);
    _backing.Position = replacementOffset;
    _backing.Write(replacement);
    Qcow2Structures.WriteUInt64BigEndianAt(
      _backing,
      l1EntryOffset,
      (ulong)replacementOffset | Qcow2Structures.CopiedFlag);
    DecrementRefcount(l2TableOffset);
    return replacementOffset;
  }

  private ulong ReadL2Entry(long guestClusterIndex, out long l2EntryOffset) => ReadL2Entry(guestClusterIndex, out l2EntryOffset, out _);

  /// <summary>Reads a guest cluster's L2 descriptor and, for extended L2 entries, its subcluster bitmap.</summary>
  private ulong ReadL2Entry(long guestClusterIndex, out long l2EntryOffset, out ulong bitmap) {
    bitmap = 0;
    var l1Index = guestClusterIndex / _l2Entries;
    var l2Index = checked((int)(guestClusterIndex % _l2Entries));
    if (l1Index < 0 || l1Index >= _header.L1Size) {
      l2EntryOffset = -1;
      return 0;
    }

    var l1EntryOffset = checked(_header.L1TableOffset + l1Index * 8L);
    var l1Entry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l1EntryOffset);
    ValidateL1Entry(l1Entry);
    var l2TableOffset = Qcow2Structures.ReadClusterOffset(l1Entry);
    if (l2TableOffset == 0) {
      l2EntryOffset = -1;
      return 0;
    }

    l2EntryOffset = checked(l2TableOffset + l2Index * (long)_header.L2EntrySize);
    if (_header.ExtendedL2)
      bitmap = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l2EntryOffset + 8);
    return Qcow2Structures.ReadUInt64BigEndianAt(_backing, l2EntryOffset);
  }

  private void ValidateL1Entry(ulong entry) {
    if ((entry & Qcow2Structures.L1ReservedMask) != 0)
      throw new InvalidDataException("QCOW2: L1 entry uses reserved bits.");
    var offset = Qcow2Structures.ReadClusterOffset(entry);
    if (offset != 0 && (offset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: L2 table is not cluster aligned.");
  }

  private void ValidateStandardL2Entry(ulong entry) {
    if ((entry & Qcow2Structures.StandardL2ReservedMask) != 0)
      throw new InvalidDataException("QCOW2: standard L2 entry uses reserved bits.");
    if (_header.ExtendedL2 && (entry & Qcow2Structures.ZeroFlag) != 0)
      throw new InvalidDataException("QCOW2: with extended L2 entries the all-zeros bit is reserved; the bitmap says which subclusters read as zeros.");
    if (_header.Version == 2 && (entry & Qcow2Structures.ZeroFlag) != 0)
      throw new InvalidDataException("QCOW2: version 2 L2 entry uses the version 3 zero flag.");
    var offset = Qcow2Structures.ReadClusterOffset(entry);
    if (offset != 0 && (offset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: guest cluster is not cluster aligned.");
  }

  private ushort GetRefcount(long hostOffset) {
    if (_header.RefcountOrder != 4)
      throw new NotSupportedException("QCOW2 mutation currently supports 16-bit refcounts only.");
    if ((hostOffset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: refcount requested for an unaligned host cluster.");

    var hostClusterIndex = hostOffset / _clusterSize;
    var tableIndex = hostClusterIndex / _header.RefcountEntriesPerBlock;
    var blockIndex = hostClusterIndex % _header.RefcountEntriesPerBlock;
    var tableEntryCapacity = checked((long)_header.RefcountTableClusters * _clusterSize / 8);
    if (tableIndex < 0 || tableIndex >= tableEntryCapacity)
      return 0;

    var tableEntryOffset = checked(_header.RefcountTableOffset + tableIndex * 8);
    var blockEntry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, tableEntryOffset);
    ValidateRefcountTableEntry(blockEntry);
    var blockOffset = Qcow2Structures.ReadRefcountBlockOffset(blockEntry);
    if (blockOffset == 0)
      return 0;
    if ((blockOffset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: refcount block is not cluster aligned.");

    return Qcow2Structures.ReadUInt16BigEndianAt(_backing, checked(blockOffset + blockIndex * 2));
  }

  private void SetRefcount(long hostOffset, ushort value) {
    if ((hostOffset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: refcount update targets an unaligned host cluster.");

    var hostClusterIndex = hostOffset / _clusterSize;
    var tableIndex = hostClusterIndex / _header.RefcountEntriesPerBlock;
    var blockIndex = hostClusterIndex % _header.RefcountEntriesPerBlock;
    var blockOffset = EnsureRefcountBlock(tableIndex);
    Qcow2Structures.WriteUInt16BigEndianAt(_backing, checked(blockOffset + blockIndex * 2), value);
  }

  private long EnsureRefcountBlock(long tableIndex) {
    var tableEntryCapacity = checked((long)_header.RefcountTableClusters * _clusterSize / 8);
    if (tableIndex < 0 || tableIndex >= tableEntryCapacity)
      throw new NotSupportedException("QCOW2 refcount table has no capacity for another host cluster.");

    var tableEntryOffset = checked(_header.RefcountTableOffset + tableIndex * 8);
    var entry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, tableEntryOffset);
    ValidateRefcountTableEntry(entry);
    var existingOffset = Qcow2Structures.ReadRefcountBlockOffset(entry);
    if (existingOffset != 0)
      return existingOffset;

    var newBlockOffset = AllocateRawCluster(clear: true);
    Qcow2Structures.WriteUInt64BigEndianAt(_backing, tableEntryOffset, (ulong)newBlockOffset);
    SetRefcount(newBlockOffset, 1);
    return newBlockOffset;
  }

  private static void ValidateRefcountTableEntry(ulong entry) {
    if ((entry & 0x1FFUL) != 0)
      throw new InvalidDataException("QCOW2: refcount table entry uses reserved low bits.");
    if ((entry & Qcow2Structures.RefcountBlockOffsetMask) != entry)
      throw new InvalidDataException("QCOW2: refcount block offset exceeds the supported signed range.");
  }

  private long AllocateRefcountedCluster(bool clear) {
    var offset = AllocateRawCluster(clear);
    SetRefcount(offset, 1);
    return offset;
  }

  private long AllocateRawCluster(bool clear) {
    var offset = Qcow2Structures.AlignUp(_backing.Length, _clusterSize);
    var newLength = checked(offset + _clusterSize);
    _backing.SetLength(newLength);
    if (clear)
      ZeroCluster(offset);
    return offset;
  }

  private void DecrementRefcount(long hostOffset) {
    var current = GetRefcount(hostOffset);
    if (current == 0)
      throw new InvalidDataException("QCOW2: attempted to release a cluster with zero refcount.");
    SetRefcount(hostOffset, checked((ushort)(current - 1)));
  }

  private void ReleaseEntryStorage(ulong entry) {
    if (entry == 0)
      return;

    if ((entry & Qcow2Structures.CompressedFlag) == 0) {
      ValidateStandardL2Entry(entry);
      var hostOffset = Qcow2Structures.ReadClusterOffset(entry);
      if (hostOffset != 0)
        DecrementRefcount(hostOffset);
      return;
    }

    var (compressedOffset, compressedLength) =
      Qcow2Structures.DecodeCompressedRange(entry, _header.ClusterBits, _backing.Length);
    var firstCluster = compressedOffset / _clusterSize;
    var lastCluster = checked((compressedOffset + compressedLength - 1L) / _clusterSize);
    for (var cluster = firstCluster; cluster <= lastCluster; ++cluster)
      DecrementRefcount(checked(cluster * (long)_clusterSize));
  }

  private void ZeroCluster(long hostOffset) {
    var zeros = new byte[_clusterSize];
    _backing.Position = hostOffset;
    _backing.Write(zeros);
  }
}
