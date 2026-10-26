using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using FileFormat.Zlib;

namespace FileFormat.Xp3;

/// <summary>One stretch of a member's bytes: stored or zlib-compressed, at an absolute archive offset.</summary>
public sealed record Xp3Segment(bool IsCompressed, long Offset, long OriginalSize, long ArchivedSize);

/// <summary>A section of a <c>File</c> index chunk other than <c>info</c>, <c>segm</c> and <c>adlr</c>, kept verbatim.</summary>
public sealed record Xp3Section(uint Tag, byte[] Body);

/// <summary>One member of a KiriKiri XP3 archive.</summary>
/// <param name="Name">Path inside the archive, '/'-separated.</param>
/// <param name="Flags">The <c>info</c> flags; bit 31 is the Releaser's "extraction protect" request.</param>
/// <param name="OriginalSize">Size of the member once every segment is decoded.</param>
/// <param name="ArchivedSize">Bytes the member occupies in the archive.</param>
/// <param name="Segments">Its segments, in order.</param>
/// <param name="Adler32">Adler-32 of the decoded member, when the index carries an <c>adlr</c> section.</param>
/// <param name="ExtraSections">Unknown sections of its index chunk.</param>
public sealed record Xp3Entry(
  string Name, uint Flags, long OriginalSize, long ArchivedSize,
  IReadOnlyList<Xp3Segment> Segments, uint? Adler32, IReadOnlyList<Xp3Section> ExtraSections) {

  /// <summary>The Releaser's "extraction protect" flag.</summary>
  public const uint ProtectFlag = 0x80000000;

  /// <summary>Whether the archive's author set the "extraction protect" flag on this member.</summary>
  public bool IsProtected => (this.Flags & ProtectFlag) != 0;

  /// <summary>
  /// Whether the name cannot be a file name: the Releaser's protect banner is an entry whose
  /// several-hundred-character name is the warning text itself.
  /// </summary>
  public bool IsBanner => this.Name.Split('/').Any(part => part.Length > 255);
}

/// <summary>
/// Reads a KiriKiri (TVP) XP3 archive, the resource container of the KiriKiri 2 / KiriKiri Z
/// visual-novel engines.
/// </summary>
/// <remarks>
/// <para>Layout (little-endian): the 11-byte magic <c>XP3\r\n \n\x1A\x8B\x67\x01</c>, an Int64
/// offset of the index, member data, the index. KiriKiri 2.32 and later put an empty
/// "continue" index at 0x17 (flag 0x80, size 0) and the real index offset at 0x20, so readers older
/// than 2.32 stop on that empty index instead of misreading the archive.</para>
/// <para>The index is a flag byte (bits 0-2: 0 = raw, 1 = zlib), then Int64 size (raw) or Int64
/// packed size + Int64 size (zlib), then a sequence of chunks <c>tag(4) Int64 size body</c>. A
/// <c>File</c> chunk carries sections: <c>info</c> (UInt32 flags, Int64 original size, Int64 archived
/// size, UInt16 name length, UTF-16LE name), <c>segm</c> (28 bytes per segment: UInt32 flags with
/// bits 0-2 = compression, Int64 offset, Int64 original size, Int64 archived size) and <c>adlr</c>
/// (UInt32 Adler-32 of the decoded member).</para>
/// <para>Converted from GARbro's <c>ArcXP3.cs</c> (MIT, morkt); see THIRD-PARTY-NOTICE.GARbro.txt.
/// Verified against archives written by the KiriKiri 2 Releaser (krkrrel 2.32 rev. 2) and read by
/// the KiriKiri Z 1.4.0 engine.</para>
/// </remarks>
public sealed class Xp3Reader {

  /// <summary>The 11-byte XP3 magic.</summary>
  public static ReadOnlySpan<byte> Magic => [(byte)'X', (byte)'P', (byte)'3', 0x0D, 0x0A, 0x20, 0x0A, 0x1A, 0x8B, 0x67, 0x01];

  internal const uint FileTag = 0x656C6946;    // "File"
  internal const uint InfoTag = 0x6F666E69;    // "info"
  internal const uint SegmTag = 0x6D676573;    // "segm"
  internal const uint AdlrTag = 0x726C6461;    // "adlr"
  internal const byte IndexContinue = 0x80;
  internal const int EncodeMask = 0x07;
  internal const int EncodeRaw = 0;
  internal const int EncodeZlib = 1;

  private readonly Stream _stream;

  /// <summary>Parses the archive header and index.</summary>
  /// <exception cref="InvalidDataException">The stream is not a well-formed XP3 archive.</exception>
  public Xp3Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var length = stream.Length;
    if (length < Magic.Length + 8)
      throw new InvalidDataException("Too short for an XP3 archive.");

    Span<byte> head = stackalloc byte[19];
    stream.Position = 0;
    stream.ReadExactly(head);
    if (!head[..11].SequenceEqual(Magic))
      throw new InvalidDataException("Not an XP3 archive (missing XP3 magic).");

    var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(head[11..]);
    var flag = ReadIndexFlag(indexOffset, length);
    if (flag == IndexContinue) {
      // KiriKiri 2.32+ "cushion": an empty raw index whose continuation is the real one.
      Span<byte> cushion = stackalloc byte[16];
      stream.Position = indexOffset + 1;
      stream.ReadExactly(cushion);
      if (BinaryPrimitives.ReadInt64LittleEndian(cushion) != 0)
        throw new InvalidDataException("XP3 continuation index is not empty.");
      this.Version = 2;
      indexOffset = BinaryPrimitives.ReadInt64LittleEndian(cushion[8..]);
      flag = ReadIndexFlag(indexOffset, length);
    }
    this.IndexOffset = indexOffset;

    byte[] index;
    stream.Position = indexOffset + 1;
    switch (flag & EncodeMask) {
      case EncodeRaw: {
        var size = this.ReadInt64();
        if (size < 0 || size > length - stream.Position)
          throw new InvalidDataException("XP3 index overruns the file.");
        index = new byte[size];
        stream.ReadExactly(index);
        break;
      }
      case EncodeZlib: {
        var packed = this.ReadInt64();
        var size = this.ReadInt64();
        if (packed < 0 || packed > length - stream.Position || size < 0 || size > int.MaxValue)
          throw new InvalidDataException("XP3 compressed index overruns the file.");
        var raw = new byte[packed];
        stream.ReadExactly(raw);
        index = ZlibStream.Decompress(raw);
        if (index.Length != size)
          throw new InvalidDataException($"XP3 index decompressed to {index.Length} bytes, header says {size}.");
        this.IndexCompressed = true;
        break;
      }
      default:
        throw new InvalidDataException($"XP3 index uses unknown encoding {flag & EncodeMask}.");
    }
    if ((flag & IndexContinue) != 0)
      throw new InvalidDataException("XP3 index chains beyond the 2.32 cushion are not defined.");

    (this.Entries, this.ForeignChunks) = ParseIndex(index, length);
  }

  /// <summary>1 for the original layout, 2 for KiriKiri 2.32+'s cushioned header.</summary>
  public int Version { get; } = 1;

  /// <summary>Whether the index is zlib-compressed.</summary>
  public bool IndexCompressed { get; }

  /// <summary>Absolute offset of the (real) index.</summary>
  public long IndexOffset { get; }

  /// <summary>Members in index order (protect banners included).</summary>
  public IReadOnlyList<Xp3Entry> Entries { get; }

  /// <summary>Tags of top-level index chunks other than <c>File</c> (name tables of obfuscated games).</summary>
  public IReadOnlyList<uint> ForeignChunks { get; }

  /// <summary>Decodes a member: segments concatenated, zlib segments inflated, Adler-32 checked.</summary>
  /// <exception cref="InvalidDataException">
  /// A segment does not decode to its declared size, or the Adler-32 recorded in the index does
  /// not match — which is what a game-specific encryption layer looks like from outside the engine.
  /// </exception>
  public byte[] Extract(Xp3Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.OriginalSize > Array.MaxLength)
      throw new InvalidDataException($"XP3 member '{entry.Name}' is too large to extract into memory.");
    var result = new byte[entry.OriginalSize];
    var pos = 0L;
    foreach (var segment in entry.Segments) {
      var raw = this.ReadSegment(segment);
      var data = segment.IsCompressed ? ZlibStream.Decompress(raw) : raw;
      if (data.Length != segment.OriginalSize || pos + data.Length > result.Length)
        throw new InvalidDataException($"XP3 member '{entry.Name}': a segment decoded to {data.Length} bytes, the index says {segment.OriginalSize}.");
      data.CopyTo(result, pos);
      pos += data.Length;
    }
    if (pos != result.Length)
      throw new InvalidDataException($"XP3 member '{entry.Name}': segments cover {pos} of {result.Length} bytes.");
    if (entry.Adler32 is { } expected && Adler32.Compute(result) != expected)
      throw new InvalidDataException(
        $"XP3 member '{entry.Name}' fails its Adler-32 check; the archive is probably encrypted with a game-specific scheme, which is not decoded.");
    return result;
  }

  /// <summary>Returns a segment's bytes exactly as stored.</summary>
  public byte[] ReadSegment(Xp3Segment segment) {
    ArgumentNullException.ThrowIfNull(segment);
    var size = segment.IsCompressed ? segment.ArchivedSize : segment.OriginalSize;
    var raw = new byte[size];
    this._stream.Position = segment.Offset;
    this._stream.ReadExactly(raw);
    return raw;
  }

  private int ReadIndexFlag(long offset, long length) {
    if (offset < Magic.Length + 8 || offset >= length - 8)
      throw new InvalidDataException($"XP3 index offset {offset} lies outside the file.");
    this._stream.Position = offset;
    return this._stream.ReadByte();
  }

  private long ReadInt64() {
    Span<byte> b = stackalloc byte[8];
    this._stream.ReadExactly(b);
    return BinaryPrimitives.ReadInt64LittleEndian(b);
  }

  private static (List<Xp3Entry>, List<uint>) ParseIndex(byte[] index, long fileLength) {
    var entries = new List<Xp3Entry>();
    var foreign = new List<uint>();
    var pos = 0;
    while (pos < index.Length) {
      if (index.Length - pos < 12)
        throw new InvalidDataException("Truncated XP3 index chunk header.");
      var tag = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(pos));
      var size = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 4));
      pos += 12;
      if (size < 0 || size > index.Length - pos)
        throw new InvalidDataException("XP3 index chunk overruns the index.");
      var body = index.AsSpan(pos, (int)size);
      pos += (int)size;
      if (tag == FileTag)
        entries.Add(ParseFile(body, fileLength));
      else
        foreign.Add(tag);
    }
    return (entries, foreign);
  }

  private static Xp3Entry ParseFile(ReadOnlySpan<byte> body, long fileLength) {
    string? name = null;
    uint flags = 0;
    long originalSize = 0, archivedSize = 0;
    uint? adler = null;
    var segments = new List<Xp3Segment>();
    var extra = new List<Xp3Section>();
    var pos = 0;
    while (pos < body.Length) {
      if (body.Length - pos < 12)
        throw new InvalidDataException("Truncated XP3 File section header.");
      var tag = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
      var size = BinaryPrimitives.ReadInt64LittleEndian(body[(pos + 4)..]);
      pos += 12;
      if (size < 0 || size > body.Length - pos)
        throw new InvalidDataException("XP3 File section overruns its chunk.");
      var s = body.Slice(pos, (int)size);
      pos += (int)size;
      switch (tag) {
        case InfoTag:
          if (s.Length < 22)
            throw new InvalidDataException("XP3 info section is too short.");
          flags = BinaryPrimitives.ReadUInt32LittleEndian(s);
          originalSize = BinaryPrimitives.ReadInt64LittleEndian(s[4..]);
          archivedSize = BinaryPrimitives.ReadInt64LittleEndian(s[12..]);
          var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(s[20..]);
          if (22 + nameLength * 2 > s.Length)
            throw new InvalidDataException("XP3 info name overruns its section.");
          name = Encoding.Unicode.GetString(s.Slice(22, nameLength * 2)).Replace('\\', '/');
          break;
        case SegmTag:
          if (s.Length % 28 != 0)
            throw new InvalidDataException("XP3 segm section is not a whole number of segments.");
          for (var i = 0; i < s.Length; i += 28) {
            var seg = s.Slice(i, 28);
            var method = BinaryPrimitives.ReadUInt32LittleEndian(seg) & EncodeMask;
            if (method > EncodeZlib)
              throw new InvalidDataException($"XP3 segment uses unknown encoding {method}.");
            var offset = BinaryPrimitives.ReadInt64LittleEndian(seg[4..]);
            var orig = BinaryPrimitives.ReadInt64LittleEndian(seg[12..]);
            var arc = BinaryPrimitives.ReadInt64LittleEndian(seg[20..]);
            var stored = method == EncodeZlib ? arc : orig;
            if (offset < Magic.Length || orig < 0 || arc < 0 || offset > fileLength - stored)
              throw new InvalidDataException("XP3 segment points outside the file.");
            segments.Add(new Xp3Segment(method == EncodeZlib, offset, orig, arc));
          }
          break;
        case AdlrTag:
          if (s.Length != 4)
            throw new InvalidDataException("XP3 adlr section is not 4 bytes.");
          adler = BinaryPrimitives.ReadUInt32LittleEndian(s);
          break;
        default:
          extra.Add(new Xp3Section(tag, s.ToArray()));
          break;
      }
    }
    if (name == null)
      throw new InvalidDataException("XP3 File chunk has no info section.");
    if (segments.Sum(g => g.OriginalSize) != originalSize)
      throw new InvalidDataException($"XP3 member '{name}': segments do not add up to its size.");
    return new Xp3Entry(name, flags, originalSize, archivedSize, segments, adler, extra);
  }
}
