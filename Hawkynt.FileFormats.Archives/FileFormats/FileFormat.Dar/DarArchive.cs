using System.Buffers.Binary;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzo;
using Compression.Core.Streams;
using FileFormat.Bzip2;
using FileFormat.Xz;
using FileFormat.Zlib;
using FileFormat.Zstd;

namespace FileFormat.Dar;

/// <summary>What a catalogue entry describes (the low five bits of its signature byte).</summary>
public enum DarEntryKind {
  /// <summary><c>d</c></summary>
  Directory,
  /// <summary><c>f</c></summary>
  File,
  /// <summary><c>l</c></summary>
  Symlink,
  /// <summary><c>c</c></summary>
  CharDevice,
  /// <summary><c>b</c></summary>
  BlockDevice,
  /// <summary><c>p</c></summary>
  Fifo,
  /// <summary><c>s</c></summary>
  Socket,
  /// <summary><c>o</c> (Solaris door)</summary>
  Door,
}

/// <summary>Whether the entry's data is in this archive (the top three bits of its signature byte).</summary>
public enum DarSavedStatus {
  /// <summary>1: the data is a binary delta against the archive of reference.</summary>
  Delta = 1,
  /// <summary>2: unchanged since the archive of reference; no data here.</summary>
  NotSaved = 2,
  /// <summary>3: data saved in this archive.</summary>
  Saved = 3,
  /// <summary>4: only the inode metadata changed; no data here.</summary>
  InodeOnly = 4,
  /// <summary>7: a placeholder from an isolated catalogue.</summary>
  Fake = 7,
}

/// <summary>One entry of a DAR catalogue, its path rebuilt from the directory nesting.</summary>
public sealed record DarEntry(
  string Path,
  DarEntryKind Kind,
  DarSavedStatus Status,
  ushort Permissions,
  ulong Uid,
  ulong Gid,
  DateTime? LastAccess,
  DateTime? LastModified,
  DateTime? LastChange,
  ulong Size,
  ulong Offset,
  ulong StorageSize,
  char Algorithm,
  byte DataFlags,
  byte[]? Checksum,
  string? LinkTarget,
  bool HasExtendedAttributes,
  bool HasFilesystemAttributes,
  ulong? HardLinkLabel) {

  /// <summary>Whether this archive carries the entry's file data.</summary>
  public bool HasData => this.Kind is DarEntryKind.File or DarEntryKind.Door && this.Status == DarSavedStatus.Saved;

  /// <summary>Whether the stored data uses dar's hole records.</summary>
  public bool IsSparse => (this.DataFlags & DarArchive.DataWithHoles) != 0;
}

/// <summary>
/// Reader for a DAR archive (archive format 9 through 11.3; verified on 11.3): the version
/// trailer and catalogue located through the two terminators at the end of the archive, and file
/// data read back through the escape, compression and sparse layers. Layout in
/// <c>docs/DAR-ON-DISK.md</c>.
/// </summary>
public sealed class DarArchive {
  /// <summary>The newest archive format this reader knows (dar 2.7.x and 2.8.x write it).</summary>
  public static readonly (int Major, int Minor) NewestFormat = (11, 3);

  internal const uint FlagScrambled = 0x20;
  internal const uint FlagTapeMarks = 0x10;
  internal const uint FlagInitialOffset = 0x08;
  internal const uint FlagCryptedKey = 0x04;
  internal const uint FlagReferenceSlicing = 0x02;
  internal const uint FlagSigned = 0x0200;
  internal const uint FlagKdf = 0x0400;
  internal const uint FlagCompressionBlockSize = 0x0800;
  internal const uint KnownFlags = 0x0E3E;
  internal const byte DataWithHoles = 0x01;
  internal const byte DataDirty = 0x02;
  internal const byte DataDeltaSignature = 0x04;
  private const int MaxEntries = 4_000_000;
  private const int MaxDepth = 4096;
  private const long MaxCatalogue = 256L << 20;
  // A version trailer is a few dozen bytes; an asymmetric-encrypted key can add a few KiB.
  private const long MaxTrailer = 1L << 20;
  private const int BlockHeaderData = 1;
  private const int BlockHeaderEof = 2;

  private readonly Stream _archive;
  private readonly long _limit;

  private DarArchive(Stream archive, long limit, DarVersionHeader trailer, byte[] dataName, string inPlace, List<DarEntry> entries) {
    this._archive = archive;
    this._limit = limit;
    this.Trailer = trailer;
    this.DataName = dataName;
    this.InPlace = inPlace;
    this.Entries = entries;
  }

  /// <summary>The version trailer that describes the archive's layers.</summary>
  public DarVersionHeader Trailer { get; }

  /// <summary>The 10-byte data name recorded at the head of the catalogue.</summary>
  public byte[] DataName { get; }

  /// <summary>The <c>-R</c> root path recorded at backup time.</summary>
  public string InPlace { get; }

  /// <summary>Every catalogue entry below the root, in catalogue order.</summary>
  public IReadOnlyList<DarEntry> Entries { get; }

  /// <summary>Reads the archive-level stream (slice headers and trailers already removed).</summary>
  /// <exception cref="InvalidDataException">The structures contradict the format.</exception>
  /// <exception cref="NotSupportedException">The archive uses a feature this reader refuses
  /// (encryption, a newer or older format, an unknown compression).</exception>
  public static DarArchive Read(Stream archive) {
    var length = archive.Length;
    var trailerAt = (long)DarPrimitives.ReadTerminator(archive, length, out var term2Start);
    if (trailerAt >= term2Start || term2Start - trailerAt > MaxTrailer)
      throw new InvalidDataException("The second DAR terminator does not point at a plausible version trailer.");
    var trailerBytes = new byte[term2Start - trailerAt];
    archive.Position = trailerAt;
    archive.ReadExactly(trailerBytes);
    var trailer = DarVersionHeader.Parse(trailerBytes, out _);
    Refuse(trailer);

    var catalogueAt = (long)DarPrimitives.ReadTerminator(archive, trailerAt, out var term1Start);
    if (catalogueAt >= term1Start || term1Start - catalogueAt > MaxCatalogue)
      throw new InvalidDataException("The first DAR terminator does not point at a plausible catalogue.");
    byte[] stored;
    if (trailer.HasTapeMarks)
      stored = DarPrimitives.ReadUnescaped(archive, catalogueAt, -1, term1Start);
    else {
      stored = new byte[term1Start - catalogueAt];
      archive.Position = catalogueAt;
      archive.ReadExactly(stored);
    }
    var catalogue = Decompress(trailer.Algorithm, trailer.BlockSize, stored, -1);

    var (dataName, inPlace, entries) = ParseCatalogue(catalogue, trailer);
    return new DarArchive(archive, term1Start, trailer, dataName, inPlace, entries);
  }

  private static void Refuse(DarVersionHeader v) {
    if (v.Major < 9 || (v.Major, v.Minor).CompareTo(NewestFormat) > 0)
      throw new NotSupportedException($"DAR archive format {v.Major}.{v.Minor} is outside the supported range 9.0 to {NewestFormat.Major}.{NewestFormat.Minor}.");
    if ((v.Flags & FlagScrambled) != 0 || (v.Flags & FlagCryptedKey) != 0)
      throw new NotSupportedException("The DAR archive is encrypted.");
    if ((v.Flags & ~KnownFlags) != 0)
      throw new NotSupportedException($"The DAR archive header has unknown flags 0x{v.Flags & ~KnownFlags:X}.");
    if (!IsKnownAlgorithm(v.Algorithm))
      throw new NotSupportedException($"DAR compression '{v.Algorithm}' is not supported.");
  }

  private static bool IsKnownAlgorithm(char a) => a is 'n' or 'z' or 'y' or 'x' or 'd' or 'q' or 'l' or 'j' or 'k';

  /// <summary>A display name for a catalogue compression letter.</summary>
  public static string AlgorithmName(char a) => a switch {
    'n' => "Stored",
    'z' => "gzip",
    'y' => "bzip2",
    'x' => "xz",
    'd' => "zstd",
    'q' => "lz4",
    'l' or 'j' or 'k' => "lzo",
    _ => $"DAR '{a}'",
  };

  /// <summary>Reads, decompresses, expands and checks the data of <paramref name="entry"/>.</summary>
  /// <exception cref="InvalidDataException">The data does not decode, or its checksum differs.</exception>
  /// <exception cref="NotSupportedException">The entry has no data in this archive.</exception>
  public byte[] ReadData(DarEntry entry) {
    if (!entry.HasData)
      throw new NotSupportedException($"'{entry.Path}' has no data in this archive ({entry.Status}).");
    if (entry.Size > (ulong)Array.MaxLength || entry.StorageSize > (ulong)Array.MaxLength)
      throw new NotSupportedException($"'{entry.Path}' is {entry.Size} bytes, more than this reader holds in memory.");
    if (entry.Offset > (ulong)this._limit || entry.StorageSize > (ulong)this._limit)
      throw new InvalidDataException($"'{entry.Path}' points outside the archive.");
    if (!IsKnownAlgorithm(entry.Algorithm))
      throw new NotSupportedException($"'{entry.Path}' uses DAR compression '{entry.Algorithm}'.");
    byte[] stored;
    if (this.Trailer.HasTapeMarks)
      stored = DarPrimitives.ReadUnescaped(this._archive, (long)entry.Offset, (long)entry.StorageSize, this._limit);
    else {
      if (entry.Offset + entry.StorageSize > (ulong)this._limit)
        throw new InvalidDataException($"'{entry.Path}' runs past the catalogue.");
      stored = new byte[entry.StorageSize];
      this._archive.Position = (long)entry.Offset;
      this._archive.ReadExactly(stored);
    }
    var data = Decompress(entry.Algorithm, entry.Algorithm == 'n' ? 0 : this.Trailer.BlockSize, stored,
      entry.IsSparse ? -1 : (long)entry.Size);
    if (entry.IsSparse)
      data = DarPrimitives.Unsparse(data, entry.Size);
    if ((ulong)data.LongLength != entry.Size)
      throw new InvalidDataException($"'{entry.Path}' decodes to {data.LongLength} bytes, the catalogue says {entry.Size}.");
    if (entry.Checksum is { } expected && !DarPrimitives.Checksum(data, expected.Length).AsSpan().SequenceEqual(expected))
      throw new InvalidDataException($"'{entry.Path}' fails its DAR data checksum.");
    return data;
  }

  internal static byte[] Decompress(char algorithm, ulong blockSize, byte[] stored, long expected) {
    try {
      if (algorithm == 'n')
        return stored;
      // lz4 and lzo always travel in dar's block framing; the others only when the archive
      // header records a compression block size.
      if (blockSize > 0 || algorithm is 'q' or 'l' or 'j' or 'k')
        return DecompressBlocks(algorithm, stored, expected, blockSize > 0 ? blockSize : StreamingBlockSize);
      return DecompressStream(algorithm, stored);
    } catch (Exception e) when (e is not (InvalidDataException or NotSupportedException or OutOfMemoryException)) {
      throw new InvalidDataException($"DAR {AlgorithmName(algorithm)} data does not decode: {e.Message}", e);
    }
  }

  private static byte[] DecompressStream(char algorithm, byte[] stored) {
    if (algorithm == 'z')
      return ZlibStream.Decompress(stored);
    using var input = new MemoryStream(stored, writable: false);
    using Stream decoder = algorithm switch {
      'y' => new Bzip2Stream(input, CompressionStreamMode.Decompress, leaveOpen: true),
      'x' => new XzStream(input, CompressionStreamMode.Decompress, leaveOpen: true),
      'd' => new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: true),
      _ => throw new NotSupportedException($"DAR compression '{algorithm}' is not supported."),
    };
    using var output = new MemoryStream();
    decoder.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] DecompressBlocks(char algorithm, byte[] stored, long expected, ulong clearBlockSize) {
    if (clearBlockSize > MaxBlock)
      throw new NotSupportedException($"DAR compression block size {clearBlockSize} exceeds this reader's limit.");
    var ceiling = (int)clearBlockSize;
    using var output = new MemoryStream(expected is > 0 and < 1 << 24 ? (int)expected : 4096);
    var pos = 0;
    while (true) {
      if (pos >= stored.Length)
        throw new InvalidDataException("DAR compressed blocks end without an EOF block.");
      var type = stored[pos++];
      var size = DarPrimitives.ReadInfinint(stored, ref pos);
      if (type == BlockHeaderEof) {
        if (size != 0)
          throw new InvalidDataException("DAR EOF block carries a size.");
        break;
      }
      if (type != BlockHeaderData || size == 0 || size > (ulong)(stored.Length - pos))
        throw new InvalidDataException($"Malformed DAR compressed block header at {pos}.");
      var block = stored.AsSpan(pos, (int)size);
      pos += (int)size;
      var decoded = algorithm switch {
        'z' => ZlibStream.Decompress(block),
        'q' => Lz4Block(block, ceiling),
        'l' or 'j' or 'k' => Lzo1xDecompressor.DecompressUpTo(block, ceiling),
        _ => DecompressStream(algorithm, block.ToArray()),
      };
      output.Write(decoded);
    }
    return output.ToArray();
  }

  /// <summary>The clear-text block size dar uses for lz4 and lzo in streaming mode.</summary>
  internal const int StreamingBlockSize = 246660;

  private const int MaxBlock = 256 << 20;

  private static byte[] Lz4Block(ReadOnlySpan<byte> block, int ceiling) {
    var buffer = new byte[ceiling];
    var n = Lz4BlockDecompressor.Decompress(block, buffer);
    return buffer.AsSpan(0, n).ToArray();
  }

  private static (byte[] DataName, string InPlace, List<DarEntry> Entries) ParseCatalogue(byte[] cat, DarVersionHeader v) {
    var c = new Cursor(cat, v);
    if (cat.Length < DarSliceHeader.NameLength)
      throw new InvalidDataException("The DAR catalogue is shorter than its data name.");
    var dataName = cat.AsSpan(0, DarSliceHeader.NameLength).ToArray();
    c.Pos = DarSliceHeader.NameLength;
    var inPlace = v.AtLeast(11, 1) ? c.String() : ".";
    var entries = new List<DarEntry>();
    var labels = new Dictionary<ulong, DarEntry>();
    var root = c.Byte();
    if (((root & 0x1F) | 0x60) != 'd')
      throw new InvalidDataException("The DAR catalogue does not start with the root directory.");
    _ = c.String(); // the root's name ("root")
    _ = ReadInode(ref c);
    ReadDirectory(ref c, "", entries, labels, 0);
    var end = c.Pos;
    var stored = c.Checksum();
    var computed = DarPrimitives.Checksum(cat.AsSpan(0, end), stored.Length);
    if (!computed.AsSpan().SequenceEqual(stored))
      throw new InvalidDataException("The DAR catalogue fails its checksum.");
    return (dataName, inPlace, entries);
  }

  private static void ReadDirectory(ref Cursor c, string prefix, List<DarEntry> entries, Dictionary<ulong, DarEntry> labels, int depth) {
    if (depth > MaxDepth)
      throw new InvalidDataException("DAR directories nest deeper than this reader allows.");
    while (true) {
      if (entries.Count >= MaxEntries)
        throw new InvalidDataException("The DAR catalogue holds more entries than this reader allows.");
      var signature = c.Byte();
      var type = (char)((signature & 0x1F) | 0x60);
      var status = signature >> 5;
      if (type == 'z')
        return;
      if (status is 0 or 5 or 6)
        throw new InvalidDataException($"DAR catalogue entry has an invalid saved status {status}.");
      var name = c.String();
      if (name.Length == 0 || name is "." or ".." || name.Contains('/'))
        throw new InvalidDataException($"DAR catalogue entry name '{name}' is not a plain file name.");
      var path = prefix + name;
      switch (type) {
        case 'x': // removed since the archive of reference: a name, the type it had, a date
          _ = c.Byte();
          _ = c.Date();
          continue;
        case 'm': {
          var label = c.Infinint();
          var flag = (char)c.Byte();
          if (flag == 'X') {
            if (!labels.TryGetValue(label, out var first))
              throw new InvalidDataException($"DAR hard link '{path}' refers to an inode not yet seen.");
            entries.Add(first with { Path = path });
            continue;
          }
          if (flag != '>')
            throw new InvalidDataException($"DAR hard link '{path}' has unknown flag 0x{(byte)flag:X2}.");
          var innerSig = c.Byte();
          var innerType = (char)((innerSig & 0x1F) | 0x60);
          var innerStatus = innerSig >> 5;
          _ = c.String(); // the inode's own (empty) name
          if (innerType is 'd' or 'm' or 'z' or 'x')
            throw new InvalidDataException($"DAR hard link '{path}' wraps a '{innerType}' entry.");
          var entry = ReadInodeEntry(ref c, path, innerType, innerStatus) with { HardLinkLabel = label };
          if (!labels.TryAdd(label, entry))
            throw new InvalidDataException($"DAR hard link label {label} is defined twice.");
          entries.Add(entry);
          continue;
        }
        case 'd': {
          var e = ReadInodeEntry(ref c, path, type, status);
          entries.Add(e);
          ReadDirectory(ref c, path + "/", entries, labels, depth + 1);
          continue;
        }
        default:
          entries.Add(ReadInodeEntry(ref c, path, type, status));
          continue;
      }
    }
  }

  private readonly record struct Inode(ushort Permissions, ulong Uid, ulong Gid, DateTime? Access, DateTime? Modified, DateTime? Change, bool Ea, bool Fsa);

  private static Inode ReadInode(ref Cursor c) {
    var flag = c.Byte();
    var ea = flag & 0x07;
    var fsa = flag & 0x18;
    if (ea is 0 or > 5)
      throw new InvalidDataException($"DAR inode has unknown EA status {ea}.");
    if (fsa == 0x18)
      throw new InvalidDataException("DAR inode has an unknown FSA status.");
    var uid = c.Infinint();
    var gid = c.Infinint();
    var perm = c.UInt16();
    var access = c.Date();
    var modified = c.Date();
    var change = c.Date();
    if (ea == 1) { // full: size, then offset and checksum in the catalogue
      _ = c.Infinint();
      _ = c.Infinint();
      _ = c.Checksum();
    }
    if (fsa != 0)
      _ = c.Infinint(); // families
    if (fsa == 0x10) { // full: size, offset, checksum
      _ = c.Infinint();
      _ = c.Infinint();
      _ = c.Checksum();
    }
    return new(perm, uid, gid, access, modified, change, ea == 1, fsa == 0x10);
  }

  private static DarEntry ReadInodeEntry(ref Cursor c, string path, char type, int status) {
    var kind = type switch {
      'd' => DarEntryKind.Directory,
      'f' => DarEntryKind.File,
      'l' => DarEntryKind.Symlink,
      'c' => DarEntryKind.CharDevice,
      'b' => DarEntryKind.BlockDevice,
      'p' => DarEntryKind.Fifo,
      's' => DarEntryKind.Socket,
      'o' => DarEntryKind.Door,
      _ => throw new NotSupportedException($"DAR catalogue entry type '{type}' is not supported."),
    };
    var saved = (DarSavedStatus)status;
    var inode = ReadInode(ref c);
    ulong size = 0, offset = 0, storage = 0;
    var algorithm = 'n';
    byte flags = 0;
    byte[]? checksum = null;
    string? target = null;
    switch (kind) {
      case DarEntryKind.File or DarEntryKind.Door:
        size = c.Infinint();
        if (saved is DarSavedStatus.Saved or DarSavedStatus.Delta) {
          offset = c.Infinint();
          storage = c.Infinint();
          flags = c.Byte();
          algorithm = (char)c.Byte();
          if ((flags & DataDeltaSignature) != 0 && saved == DarSavedStatus.Delta && c.Version.AtLeast(11, 2))
            _ = c.Checksum(); // base checksum of the patch
          checksum = c.Checksum();
        } else if (c.Version.AtLeast(10, 0))
          flags = c.Byte();
        if ((flags & DataDeltaSignature) != 0) {
          if (!c.Version.AtLeast(11, 2))
            _ = c.Checksum();
          var signatureSize = c.Infinint();
          if (signatureSize != 0)
            _ = c.Infinint();
          _ = c.Checksum();
        }
        flags &= unchecked((byte)~DataDirty);
        break;
      case DarEntryKind.Symlink when saved == DarSavedStatus.Saved:
        target = c.String();
        break;
      case DarEntryKind.CharDevice or DarEntryKind.BlockDevice when saved == DarSavedStatus.Saved:
        _ = c.UInt16();
        _ = c.UInt16();
        break;
    }
    return new DarEntry(path, kind, saved, inode.Permissions, inode.Uid, inode.Gid, inode.Access, inode.Modified,
      inode.Change, size, offset, storage, algorithm, flags, checksum, target, inode.Ea, inode.Fsa, null);
  }

  private struct Cursor(byte[] data, DarVersionHeader version) {
    public int Pos;
    public readonly DarVersionHeader Version => version;

    public byte Byte() {
      if (this.Pos >= data.Length)
        throw new InvalidDataException("The DAR catalogue ends inside an entry.");
      return data[this.Pos++];
    }

    public ushort UInt16() {
      if (this.Pos + 2 > data.Length)
        throw new InvalidDataException("The DAR catalogue ends inside an entry.");
      var v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(this.Pos));
      this.Pos += 2;
      return v;
    }

    public ulong Infinint() => DarPrimitives.ReadInfinint(data, ref this.Pos);
    public string String() => DarPrimitives.ReadString(data, ref this.Pos);
    public byte[] Checksum() => DarPrimitives.ReadChecksum(data, ref this.Pos);

    public DateTime? Date() {
      var unit = (char)this.Byte();
      var seconds = this.Infinint();
      ulong fraction = 0;
      long ticksPerUnit;
      switch (unit) {
        case 's': ticksPerUnit = 0; break;
        case 'u': fraction = this.Infinint(); ticksPerUnit = 10; break;
        case 'n': fraction = this.Infinint(); ticksPerUnit = -100; break;
        default: throw new InvalidDataException($"DAR date has unknown unit '{unit}'.");
      }
      if (seconds == 0 && fraction == 0)
        return null;
      if (seconds > 253402300799UL)
        return null;
      var ticks = ticksPerUnit switch {
        10 => (long)Math.Min(fraction, 999_999UL) * 10,
        -100 => (long)Math.Min(fraction, 999_999_999UL) / 100,
        _ => 0,
      };
      return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(ticks);
    }
  }
}
