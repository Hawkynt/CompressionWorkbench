#pragma warning disable CS1591

namespace FileFormat.Qcow2;

/// <summary>
/// Seekable access to the active guest disk of a QCOW2 image.
/// Reads resolve standard, zero, unallocated and compressed clusters (deflate or zstd),
/// extended L2 entries with their 32 subclusters, and backing files (QCOW2 or raw), which
/// supply every cluster or subcluster the image leaves unallocated. AES- and LUKS-encrypted
/// images are read when opened with their password (<see cref="Qcow2Encryption"/>).
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
  private readonly Qcow2Encryption? _encryption;
  private readonly Stream? _dataFile;
  private long _position;

  private Qcow2Stream(Stream backing, Qcow2Header header, bool leaveOpen, Stream? parent, Qcow2Encryption? encryption,
      Stream? dataFile) {
    _backing = backing;
    _header = header;
    _clusterSize = header.ClusterSize;
    _l2Entries = header.L2Entries;
    _writableProfile = backing.CanWrite && Qcow2Structures.IsWritableProfile(header, keyed: encryption != null)
      && (dataFile?.CanWrite ?? true);
    _leaveOpen = leaveOpen;
    _parent = parent;
    _encryption = encryption;
    _dataFile = dataFile;
    _refcountsTrusted = (header.IncompatibleFeatures & Qcow2Structures.IncompatibleDirty) == 0;
  }

  /// <summary>False while the dirty bit says lazy refcounts may be stale; repaired before the first write.</summary>
  private bool _refcountsTrusted;

  /// <summary>Where the guest clusters are: the external data file, or the image itself.</summary>
  private Stream Guest => _dataFile ?? _backing;

  /// <summary>Attempts to open a supported QCOW2 image as a virtual guest-disk stream.</summary>
  public static Qcow2Stream? TryOpen(Stream backing, bool leaveOpen = true) => TryOpen(backing, password: null, leaveOpen);

  /// <summary>Attempts to open a supported QCOW2 image, unlocking an encrypted one with <paramref name="password"/>.</summary>
  public static Qcow2Stream? TryOpen(Stream backing, string? password, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(backing);
    if (!backing.CanRead || !backing.CanSeek)
      return null;
    try {
      return Open(backing, password, leaveOpen);
    } catch (InvalidDataException) {
      return null;
    } catch (UnauthorizedAccessException) {
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
  public static Qcow2Stream Open(Stream backing, bool leaveOpen = true) => Open(backing, null, leaveOpen, depth: 0);

  /// <summary>Opens a QCOW2 image, unlocking an encrypted one (and encrypted backing images) with <paramref name="password"/>.</summary>
  /// <exception cref="UnauthorizedAccessException">The image is encrypted and the password is missing or wrong.</exception>
  public static Qcow2Stream Open(Stream backing, string? password, bool leaveOpen = true) => Open(backing, password, leaveOpen, depth: 0);

  private static Qcow2Stream Open(Stream backing, string? password, bool leaveOpen, int depth) {
    ArgumentNullException.ThrowIfNull(backing);
    var header = Qcow2Structures.ReadHeader(backing);
    Qcow2Structures.ValidateReadableProfile(header);
    var encryption = header.CryptMethod != Qcow2Structures.CryptNone ? Qcow2Encryption.Open(backing, header, password) : null;
    Stream? parent = null;
    try {
      parent = header.HasBackingFile ? OpenBackingFile(backing, header, password, depth) : null;
      var dataFile = header.ExternalData ? OpenDataFile(backing, header) : null;
      return new Qcow2Stream(backing, header, leaveOpen, parent, encryption, dataFile);
    } catch {
      parent?.Dispose();
      encryption?.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Opens the external data file the image's guest clusters live in (qcow2.txt, incompatible
  /// feature bit 2): named by the data-file header extension, found relative to the image's
  /// directory as QEMU finds it, and opened for writing when the image is.
  /// </summary>
  private static FileStream OpenDataFile(Stream image, in Qcow2Header header) {
    var name = header.DataFileName;
    if (string.IsNullOrEmpty(name))
      throw new NotSupportedException("QCOW2: the image keeps its guest data in an external data file but does not name it.");
    if (name.Contains(':') && !Path.IsPathRooted(name))
      throw new NotSupportedException($"QCOW2: data file '{name}' is a protocol URL, not a file.");
    if (image is not FileStream file)
      throw new NotSupportedException($"QCOW2: the image keeps its guest data in '{name}'; open it from a file path so that file can be found.");
    var directory = Path.GetDirectoryName(Path.GetFullPath(file.Name)) ?? ".";
    var path = Path.IsPathRooted(name) ? name : Path.Combine(directory, name);
    if (!File.Exists(path))
      throw new InvalidDataException($"QCOW2: data file '{name}' was not found next to the image.");
    return new FileStream(path, FileMode.Open, file.CanWrite ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite);
  }

  /// <summary>Opens the image's backing file, QCOW2 (recursively) or raw.</summary>
  private static Stream OpenBackingFile(Stream image, in Qcow2Header header, string? password, int depth) {
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
        "qcow2" => Open(parentFile, password, leaveOpen: false, depth + 1),
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
        "QCOW2 write access requires a writable image that is not marked corrupt, has no unknown " +
        "incompatible or autoclear features, and, when encrypted, was opened with its password.");
    if (_position < 0 || _position > Length - buffer.Length)
      throw new EndOfStreamException("Write would extend beyond the fixed QCOW2 virtual disk size.");
    if (!_refcountsTrusted)
      RepairLazyRefcounts();

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

  public override void Flush() {
    _dataFile?.Flush();
    _backing.Flush();
  }

  protected override void Dispose(bool disposing) {
    if (disposing) {
      _parent?.Dispose();
      _dataFile?.Dispose();
      _encryption?.Dispose();
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
      if (_encryption != null)
        throw new InvalidDataException("QCOW2: an encrypted image cannot hold compressed clusters.");
      if (_dataFile != null)
        throw new InvalidDataException("QCOW2: an image with an external data file cannot hold compressed clusters.");
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
      if (allocated != 0 && hostOffset == 0 && !this.MapsToDataFileStart(entry))
        throw new InvalidDataException("QCOW2: allocated subclusters without a host cluster.");
      var done = 0;
      while (done < destination.Length) {
        var inCluster = inClusterOffset + done;
        var subcluster = inCluster / subclusterSize;
        var length = Math.Min(destination.Length - done, (subcluster + 1) * subclusterSize - inCluster);
        var part = destination.Slice(done, length);
        if ((allocated & (1u << subcluster)) != 0)
          this.ReadHost(hostOffset, guestOffset - inClusterOffset, inCluster, part);
        else if ((zero & (1u << subcluster)) == 0)
          this.ReadFromParent(checked(guestOffset + done), part);
        done += length;
      }
      return;
    }

    if (_header.Version >= 3 && (entry & Qcow2Structures.ZeroFlag) != 0)
      return;
    if (hostOffset == 0 && !this.MapsToDataFileStart(entry)) {
      if ((entry & Qcow2Structures.CopiedFlag) != 0)
        throw new InvalidDataException("QCOW2: self-contained standard cluster has copied flag but zero host offset.");
      this.ReadFromParent(guestOffset, destination);
      return;
    }

    this.ReadHost(hostOffset, guestOffset - inClusterOffset, inClusterOffset, destination);
  }

  /// <summary>
  /// Whether an entry with host offset zero is the cluster at offset zero of an external data
  /// file rather than an unallocated one: there zero is a real offset, and a cluster in a data
  /// file always has refcount one, so the copied flag tells the two apart (as QEMU reads it).
  /// </summary>
  private bool MapsToDataFileStart(ulong entry)
    => _dataFile != null && (entry & Qcow2Structures.CopiedFlag) != 0;

  /// <summary>Reads guest bytes stored in the host cluster at <paramref name="hostCluster"/>, decrypting whole sectors when the image is encrypted.</summary>
  private void ReadHost(long hostCluster, long guestCluster, int inCluster, Span<byte> destination) {
    if (_dataFile != null && hostCluster != guestCluster)
      throw new InvalidDataException(
        $"QCOW2: guest cluster at {guestCluster} maps to {hostCluster} in the external data file; the offsets must match.");
    if (_encryption == null) {
      this.ReadGuestStore(checked(hostCluster + inCluster), destination);
      return;
    }
    var start = inCluster & ~511;
    var end = (inCluster + destination.Length + 511) & ~511;
    var sectors = new byte[end - start];
    this.ReadGuestStore(checked(hostCluster + start), sectors);
    _encryption.Decrypt(hostCluster + start, guestCluster + start, sectors);
    sectors.AsSpan(inCluster - start, destination.Length).CopyTo(destination);
  }

  /// <summary>
  /// Reads guest-cluster bytes from where they are stored. An external data file may end inside
  /// its last cluster (a raw one is exactly as long as the guest disk); what lies past its end
  /// reads as zeros, as QEMU reads it.
  /// </summary>
  private void ReadGuestStore(long offset, Span<byte> destination) {
    if (_dataFile == null) {
      Qcow2Structures.ReadExactlyAt(_backing, offset, destination);
      return;
    }
    destination.Clear();
    var available = (int)Math.Clamp(_dataFile.Length - offset, 0, destination.Length);
    if (available == 0)
      return;
    _dataFile.Position = offset;
    _dataFile.ReadExactly(destination[..available]);
  }

  /// <summary>Fills <paramref name="destination"/> from the backing file; zeros where it is shorter or absent.</summary>
  private void ReadFromParent(long guestOffset, Span<byte> destination) {
    if (_parent == null || guestOffset >= _parent.Length)
      return;
    var available = (int)Math.Min(destination.Length, _parent.Length - guestOffset);
    _parent.Position = guestOffset;
    _parent.ReadExactly(destination[..available]);
  }

  /// <summary>
  /// Writes into an image whose guest clusters live in an external data file: each cluster sits
  /// at its own guest offset there and is not refcounted (qcow2.txt, incompatible bit 2), so a
  /// write lands in place and an unallocated or zero cluster is materialised whole (zeros around
  /// the written bytes, which keeps a raw data file a consistent image) and mapped to itself.
  /// </summary>
  private void WriteExternalGuestRange(long guestClusterIndex, int inClusterOffset, ReadOnlySpan<byte> source) {
    var data = _dataFile!;
    var l2TableOffset = EnsureExclusiveL2Table(guestClusterIndex);
    var l2EntryOffset = checked(l2TableOffset + (guestClusterIndex % _l2Entries) * (long)_header.L2EntrySize);
    var oldEntry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l2EntryOffset);
    if ((oldEntry & Qcow2Structures.CompressedFlag) != 0)
      throw new InvalidDataException("QCOW2: an image with an external data file cannot hold compressed clusters.");
    ValidateStandardL2Entry(oldEntry);

    var guestCluster = checked(guestClusterIndex * _clusterSize);
    var hostOffset = Qcow2Structures.ReadClusterOffset(oldEntry);
    if (hostOffset != 0 && hostOffset != guestCluster)
      throw new InvalidDataException(
        $"QCOW2: guest cluster at {guestCluster} maps to {hostOffset} in the external data file; the offsets must match.");
    var zero = !_header.ExtendedL2 && _header.Version >= 3 && (oldEntry & Qcow2Structures.ZeroFlag) != 0;
    if ((hostOffset != 0 || this.MapsToDataFileStart(oldEntry)) && !zero && _encryption == null && !_header.ExtendedL2) {
      data.Position = checked(guestCluster + inClusterOffset);
      data.Write(source);
      if ((oldEntry & Qcow2Structures.CopiedFlag) == 0)
        Qcow2Structures.WriteUInt64BigEndianAt(_backing, l2EntryOffset, oldEntry | Qcow2Structures.CopiedFlag);
      return;
    }

    // Anything else is rewritten as a whole cluster as the guest sees it (subclusters from the
    // backing file, zeros, or deciphered bytes), patched, enciphered for its offset, and mapped
    // to itself with every subcluster allocated.
    var cluster = new byte[_clusterSize];
    ReadGuestRange(guestClusterIndex, 0, cluster);
    source.CopyTo(cluster.AsSpan(inClusterOffset));
    _encryption?.Encrypt(guestCluster, guestCluster, cluster);
    var length = (int)Math.Min(_clusterSize, _header.VirtualSize - guestCluster);
    if (_encryption != null)
      length = (length + 511) & ~511;
    data.Position = guestCluster;
    data.Write(cluster, 0, length);
    this.WriteMapping(l2EntryOffset, guestCluster);
  }

  /// <summary>Maps an L2 entry to the whole host cluster at <paramref name="hostOffset"/>, every subcluster allocated.</summary>
  private void WriteMapping(long l2EntryOffset, long hostOffset) {
    Qcow2Structures.WriteUInt64BigEndianAt(_backing, l2EntryOffset, (ulong)hostOffset | Qcow2Structures.CopiedFlag);
    if (_header.ExtendedL2)
      Qcow2Structures.WriteUInt64BigEndianAt(_backing, l2EntryOffset + 8, 0xFFFF_FFFFUL);
  }

  private void WriteGuestRange(long guestClusterIndex, int inClusterOffset, ReadOnlySpan<byte> source) {
    if (_dataFile != null) {
      this.WriteExternalGuestRange(guestClusterIndex, inClusterOffset, source);
      return;
    }
    var l2TableOffset = EnsureExclusiveL2Table(guestClusterIndex);
    var l2Index = checked((int)(guestClusterIndex % _l2Entries));
    var l2EntryOffset = checked(l2TableOffset + l2Index * (long)_header.L2EntrySize);
    var oldEntry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l2EntryOffset);

    // A cluster this image alone owns is rewritten where it is; a shared, compressed or
    // unallocated one is copied out to a fresh cluster (copy-on-write: a backing file and a
    // snapshot's or a compressed neighbour's bytes are never touched).
    var compressed = (oldEntry & Qcow2Structures.CompressedFlag) != 0;
    long oldHostOffset = 0;
    var exclusive = false;
    if (!compressed) {
      ValidateStandardL2Entry(oldEntry);
      oldHostOffset = Qcow2Structures.ReadClusterOffset(oldEntry);
      if (oldHostOffset != 0) {
        var refcount = GetRefcount(oldHostOffset);
        if (refcount == 0)
          throw new InvalidDataException("QCOW2: allocated guest cluster has a zero refcount.");
        exclusive = refcount == 1;
      } else if ((oldEntry & Qcow2Structures.CopiedFlag) != 0) {
        throw new InvalidDataException("QCOW2: self-contained standard cluster has copied flag but zero host offset.");
      }
    }

    var zeroFlag = !_header.ExtendedL2 && _header.Version >= 3 && (oldEntry & Qcow2Structures.ZeroFlag) != 0;
    if (exclusive && !zeroFlag && _encryption == null && !_header.ExtendedL2) {
      if ((oldEntry & Qcow2Structures.CopiedFlag) == 0)
        Qcow2Structures.WriteUInt64BigEndianAt(_backing, l2EntryOffset, oldEntry | Qcow2Structures.CopiedFlag);
      _backing.Position = checked(oldHostOffset + inClusterOffset);
      _backing.Write(source);
      return;
    }

    // The whole cluster as the guest sees it (zeros, subclusters from the backing file,
    // decompressed or deciphered bytes), patched, enciphered for where it lands.
    var cluster = new byte[_clusterSize];
    if (inClusterOffset != 0 || source.Length != _clusterSize)
      ReadGuestRange(guestClusterIndex, 0, cluster);
    source.CopyTo(cluster.AsSpan(inClusterOffset));

    var target = exclusive ? oldHostOffset : AllocateRefcountedCluster(clear: false);
    _encryption?.Encrypt(target, guestClusterIndex * _clusterSize, cluster);
    _backing.Position = target;
    _backing.Write(cluster);
    this.WriteMapping(l2EntryOffset, target);

    if (!exclusive)
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

  /// <summary>
  /// Brings the refcounts of an image left dirty by lazy refcounting back to what its tables
  /// say, as <c>qemu-img check -r all</c> does, then clears the dirty bit: every host cluster is
  /// counted once per L1 table (active and each snapshot's) that reaches it, plus the header,
  /// L1 tables, snapshot table, refcount table and blocks and LUKS header; the copied flags of the
  /// active tables are set where a count is one and cleared elsewhere.
  /// </summary>
  private void RepairLazyRefcounts() {
    var counts = new Dictionary<long, ulong>();
    void Count(long offset, long length) {
      if (length <= 0) return;
      for (var cluster = offset / _clusterSize; cluster <= (offset + length - 1) / _clusterSize; ++cluster)
        counts[cluster] = counts.GetValueOrDefault(cluster) + 1;
    }

    Count(0, _clusterSize);
    var l1Tables = new List<(long Offset, int Size)> { (_header.L1TableOffset, _header.L1Size) };
    if (_header.SnapshotCount != 0) {
      var (snapshots, tableBytes) = Qcow2Structures.ReadSnapshotTable(_backing, _header);
      Count(_header.SnapshotsOffset, tableBytes);
      l1Tables.AddRange(snapshots.Select(s => (s.L1TableOffset, s.L1Size)));
    }
    var l2 = new byte[_clusterSize];
    foreach (var (l1Offset, l1Size) in l1Tables) {
      Count(l1Offset, l1Size * 8L);
      for (var i = 0; i < l1Size; ++i) {
        var l2Offset = Qcow2Structures.ReadClusterOffset(Qcow2Structures.ReadUInt64BigEndianAt(_backing, l1Offset + i * 8L));
        if (l2Offset == 0) continue;
        Count(l2Offset, _clusterSize);
        Qcow2Structures.ReadExactlyAt(_backing, l2Offset, l2);
        for (var at = 0; at + _header.L2EntrySize <= l2.Length; at += _header.L2EntrySize) {
          var entry = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(l2.AsSpan(at));
          if ((entry & Qcow2Structures.CompressedFlag) != 0) {
            var (offset, length) = Qcow2Structures.DecodeCompressedRange(entry, _header.ClusterBits, _backing.Length);
            Count(offset, length);
          } else if (_dataFile == null && Qcow2Structures.ReadClusterOffset(entry) is var host and not 0) {
            Count(host, _clusterSize);
          }
        }
      }
    }
    Count(_header.RefcountTableOffset, (long)_header.RefcountTableClusters * _clusterSize);
    var tableEntries = (long)_header.RefcountTableClusters * _clusterSize / 8;
    var blocks = new List<(long Index, long Offset)>();
    for (var i = 0L; i < tableEntries; ++i) {
      var block = Qcow2Structures.ReadRefcountBlockOffset(Qcow2Structures.ReadUInt64BigEndianAt(_backing, _header.RefcountTableOffset + i * 8));
      if (block == 0) continue;
      Count(block, _clusterSize);
      blocks.Add((i, block));
    }
    if (_header.CryptMethod == Qcow2Structures.CryptLuks)
      Count(_header.FdeOffset, _header.FdeLength);

    // Every entry of every existing block gets its true count (zero where nothing points);
    // a counted cluster no block covers gets a new block (which counts itself).
    var perBlock = _header.RefcountEntriesPerBlock;
    var covered = new HashSet<long>();
    foreach (var (index, block) in blocks) {
      for (var e = 0L; e < perBlock; ++e) {
        var cluster = index * perBlock + e;
        var want = counts.GetValueOrDefault(cluster);
        if (ReadRefcountEntry(block, e) != want)
          WriteRefcountEntry(block, e, want);
        covered.Add(cluster);
      }
    }
    foreach (var (cluster, count) in counts)
      if (!covered.Contains(cluster))
        SetRefcount(cluster * _clusterSize, count);

    // The active tables' copied flags follow the counts.
    for (var i = 0; i < _header.L1Size; ++i) {
      var l1EntryOffset = _header.L1TableOffset + i * 8L;
      var l1Entry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l1EntryOffset);
      var l2Offset = Qcow2Structures.ReadClusterOffset(l1Entry);
      if (l2Offset == 0) continue;
      var fixedL1 = counts.GetValueOrDefault(l2Offset / _clusterSize) == 1
        ? l1Entry | Qcow2Structures.CopiedFlag : l1Entry & ~Qcow2Structures.CopiedFlag;
      if (fixedL1 != l1Entry) Qcow2Structures.WriteUInt64BigEndianAt(_backing, l1EntryOffset, fixedL1);
      if (_dataFile != null) continue;
      for (var at = 0; at + _header.L2EntrySize <= _clusterSize; at += _header.L2EntrySize) {
        var entry = Qcow2Structures.ReadUInt64BigEndianAt(_backing, l2Offset + at);
        var host = Qcow2Structures.ReadClusterOffset(entry);
        if ((entry & Qcow2Structures.CompressedFlag) != 0 || host == 0) continue;
        var fixedL2 = counts.GetValueOrDefault(host / _clusterSize) == 1
          ? entry | Qcow2Structures.CopiedFlag : entry & ~Qcow2Structures.CopiedFlag;
        if (fixedL2 != entry) Qcow2Structures.WriteUInt64BigEndianAt(_backing, l2Offset + at, fixedL2);
      }
    }

    var incompatible = Qcow2Structures.ReadUInt64BigEndianAt(_backing, 72);
    Qcow2Structures.WriteUInt64BigEndianAt(_backing, 72, incompatible & ~Qcow2Structures.IncompatibleDirty);
    _backing.Flush();
    _refcountsTrusted = true;
  }

  /// <summary>
  /// Reads entry <paramref name="index"/> of the refcount block at <paramref name="blockOffset"/>:
  /// 2^refcount_order bits each, big-endian from 8 bits up, packed from the least significant
  /// bit of each byte below that (as QEMU packs them).
  /// </summary>
  private ulong ReadRefcountEntry(long blockOffset, long index) {
    var bits = _header.RefcountBits;
    if (bits >= 8) {
      Span<byte> buffer = stackalloc byte[8];
      var bytes = bits / 8;
      Qcow2Structures.ReadExactlyAt(_backing, checked(blockOffset + index * bytes), buffer[..bytes]);
      ulong value = 0;
      for (var i = 0; i < bytes; ++i) value = (value << 8) | buffer[i];
      return value;
    }
    Span<byte> one = stackalloc byte[1];
    var perByte = 8 / bits;
    Qcow2Structures.ReadExactlyAt(_backing, checked(blockOffset + index / perByte), one);
    return (ulong)((one[0] >> (int)(index % perByte * bits)) & ((1 << bits) - 1));
  }

  private void WriteRefcountEntry(long blockOffset, long index, ulong value) {
    var bits = _header.RefcountBits;
    if (bits < 64 && value >> bits != 0)
      throw new NotSupportedException($"QCOW2: refcount {value} does not fit the image's {bits}-bit refcounts.");
    if (bits >= 8) {
      Span<byte> buffer = stackalloc byte[8];
      var bytes = bits / 8;
      for (var i = bytes - 1; i >= 0; --i, value >>= 8) buffer[i] = (byte)value;
      _backing.Position = checked(blockOffset + index * bytes);
      _backing.Write(buffer[..bytes]);
      return;
    }
    Span<byte> one = stackalloc byte[1];
    var perByte = 8 / bits;
    var at = checked(blockOffset + index / perByte);
    Qcow2Structures.ReadExactlyAt(_backing, at, one);
    var shift = (int)(index % perByte * bits);
    var mask = ((1 << bits) - 1) << shift;
    one[0] = (byte)((one[0] & ~mask) | ((int)value << shift));
    _backing.Position = at;
    _backing.Write(one);
  }

  private ulong GetRefcount(long hostOffset) {
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

    return ReadRefcountEntry(blockOffset, blockIndex);
  }

  private void SetRefcount(long hostOffset, ulong value) {
    if ((hostOffset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: refcount update targets an unaligned host cluster.");

    var hostClusterIndex = hostOffset / _clusterSize;
    var tableIndex = hostClusterIndex / _header.RefcountEntriesPerBlock;
    var blockIndex = hostClusterIndex % _header.RefcountEntriesPerBlock;
    var blockOffset = EnsureRefcountBlock(tableIndex);
    WriteRefcountEntry(blockOffset, blockIndex, value);
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
    SetRefcount(hostOffset, current - 1);
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
