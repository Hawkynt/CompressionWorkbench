using System.Text;
using Compression.Core.Checksums;
using FileFormat.Bzip2;
using FileFormat.Zlib;

namespace FileFormat.Ewf;

/// <summary>
/// An EWF2-Ex01 segment set (EnCase 7 "Evidence File Format version 2"), after libyal's
/// <i>EWF 2 specification</i>: a 32-byte file header, then sections whose 64-byte descriptor
/// <i>follows</i> their data and points back at the previous descriptor, read from the end.
/// </summary>
/// <remarks>
/// The media is the chunk list of every sector table (section 4) across the segments, sized by
/// the device information (sectors × bytes per sector) and chunked by the case data (sectors per
/// chunk). Chunks are zlib or bzip2 compressed (by the file header's method), stored with an
/// Adler-32 trailer, stored plain, or pattern-filled. Encrypted sets and Lx01 (logical) sets are
/// refused.
/// </remarks>
public sealed class Ewf2Image : IDisposable {
  /// <summary>"EVF2\r\n\x81\0".</summary>
  public static ReadOnlySpan<byte> Signature => [0x45, 0x56, 0x46, 0x32, 0x0D, 0x0A, 0x81, 0x00];
  /// <summary>"LEF2\r\n\x81\0" (Lx01, logical evidence).</summary>
  public static ReadOnlySpan<byte> LogicalSignature => [0x4C, 0x45, 0x46, 0x32, 0x0D, 0x0A, 0x81, 0x00];

  internal const int FileHeaderSize = 32, DescriptorSize = 64;
  internal const uint TypeDeviceInformation = 1, TypeCaseData = 2, TypeSectorData = 3, TypeSectorTable = 4,
    TypeMd5 = 8, TypeSha1 = 9, TypeNext = 0xD, TypeDone = 0xF;
  internal const uint SectionEncrypted = 2;
  internal const uint ChunkCompressed = 1, ChunkChecksummed = 2, ChunkPatternFill = 4;
  internal const ushort CompressionNone = 0, CompressionZlib = 1, CompressionBzip2 = 2;
  private const int MaxSegments = 1000;

  /// <summary>One section: its type and where its data lies in its segment.</summary>
  public sealed record Section(int Segment, uint Type, uint Flags, long DataOffset, long DataSize, uint PaddingSize);

  /// <summary>One media chunk as a sector table records it.</summary>
  internal readonly record struct Chunk(int Segment, ulong Offset, uint Size, uint Flags);

  private readonly List<Stream> _segments;
  private readonly bool _leaveFirstOpen;

  private Ewf2Image(List<Stream> segments, bool leaveFirstOpen) {
    this._segments = segments;
    this._leaveFirstOpen = leaveFirstOpen;
  }

  /// <summary>The chunk compression method of the file header.</summary>
  public ushort CompressionMethod { get; private set; }
  /// <summary>The segment file set identifier.</summary>
  public Guid SetIdentifier { get; private set; }
  /// <summary>Every section of every segment, in file order.</summary>
  public IReadOnlyList<Section> Sections => this._sections;
  /// <summary>Device information values by tag (sn, md, ts, bp, ...).</summary>
  public IReadOnlyDictionary<string, string> DeviceInformation { get; private set; } = new Dictionary<string, string>();
  /// <summary>Case data values by tag (nm, cn, en, ex, nt, sb, ...).</summary>
  public IReadOnlyDictionary<string, string> CaseData { get; private set; } = new Dictionary<string, string>();
  /// <summary>Bytes per sector.</summary>
  public int BytesPerSector { get; private set; }
  /// <summary>Sectors per chunk.</summary>
  public int SectorsPerChunk { get; private set; }
  /// <summary>Media size in bytes.</summary>
  public long MediaSize { get; private set; }
  /// <summary>The stored MD5 of the media, if any.</summary>
  public byte[]? Md5 { get; private set; }
  /// <summary>The stored SHA-1 of the media, if any.</summary>
  public byte[]? Sha1 { get; private set; }
  /// <summary>Number of segment files in the set.</summary>
  public int SegmentCount => this._segments.Count;

  private readonly List<Section> _sections = [];
  internal List<Chunk> Chunks { get; } = [];

  /// <summary>
  /// Opens the set <paramref name="first"/> starts; further segments (.Ex02 ...) are looked up next
  /// to it when it was opened from a file.
  /// </summary>
  /// <exception cref="InvalidDataException">The set is malformed or incomplete.</exception>
  /// <exception cref="NotSupportedException">Encrypted, logical (Lx01) or not reachable from a file.</exception>
  public static Ewf2Image Open(Stream first, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(first);
    var image = new Ewf2Image([first], leaveOpen);
    try {
      image.Load(first);
      return image;
    } catch {
      image.Dispose();
      throw;
    }
  }

  private void Load(Stream first) {
    var header = ReadAt(first, 0, FileHeaderSize);
    if (header.AsSpan(0, 8).SequenceEqual(LogicalSignature))
      throw new NotSupportedException("EWF2: Lx01 logical evidence sets are not supported.");
    if (!header.AsSpan(0, 8).SequenceEqual(Signature))
      throw new InvalidDataException("EWF2: missing the EVF2 signature.");
    if (header[8] != 2)
      throw new NotSupportedException($"EWF2: format version {header[8]}.{header[9]} is not supported.");
    this.CompressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
    this.SetIdentifier = new Guid(header.AsSpan(16, 16));

    for (var segment = 0; ; ++segment) {
      var stream = this._segments[segment];
      if (segment > 0) {
        var h = ReadAt(stream, 0, FileHeaderSize);
        if (!h.AsSpan(0, 8).SequenceEqual(Signature) || BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)) != segment + 1)
          throw new InvalidDataException($"EWF2: segment file {segment + 1} does not continue the set.");
        if (new Guid(h.AsSpan(16, 16)) != this.SetIdentifier)
          throw new InvalidDataException($"EWF2: segment file {segment + 1} belongs to another set.");
      }
      var last = this.ReadSections(segment, stream);
      if (last == TypeDone)
        break;
      if (last != TypeNext)
        throw new InvalidDataException("EWF2: a segment ends in neither 'next' nor 'done'; the set is incomplete.");
      if (this._segments[0] is not FileStream file)
        throw new NotSupportedException("EWF2: the image continues in further segment files; open it from a file path so they can be found.");
      if (this._segments.Count >= MaxSegments)
        throw new InvalidDataException("EWF2: the segment set has too many files.");
      var path = SegmentPath(file.Name, this._segments.Count + 1);
      if (!File.Exists(path))
        throw new InvalidDataException($"EWF2: segment file '{Path.GetFileName(path)}' is missing.");
      this._segments.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    }

    this.BytesPerSector = Number(this.DeviceInformation, "bp", 512);
    this.SectorsPerChunk = Number(this.CaseData, "sb", 64);
    if (this.BytesPerSector is <= 0 or > 65536 || this.SectorsPerChunk is <= 0 or > 32768)
      throw new InvalidDataException("EWF2: implausible sector or chunk geometry.");
    var chunkSize = (long)this.BytesPerSector * this.SectorsPerChunk;
    this.MediaSize = this.DeviceInformation.TryGetValue("ts", out var ts) && long.TryParse(ts, out var sectors) && sectors > 0
      ? checked(sectors * this.BytesPerSector)
      : this.Chunks.Count * chunkSize;
    if (this.MediaSize > this.Chunks.Count * chunkSize || this.MediaSize <= (this.Chunks.Count - 1) * chunkSize && this.Chunks.Count > 0)
      throw new InvalidDataException($"EWF2: {this.Chunks.Count} chunks do not hold a {this.MediaSize}-byte medium.");
  }

  /// <summary>Reads one segment's sections back to front; returns the type of its last section.</summary>
  private uint ReadSections(int segment, Stream stream) {
    var found = new List<Section>();
    var cursor = stream.Length - DescriptorSize;
    var lastType = 0u;
    for (var guard = 0; ; ++guard) {
      if (guard > 1_000_000 || cursor < FileHeaderSize)
        throw new InvalidDataException("EWF2: the section chain is broken.");
      var d = ReadAt(stream, cursor, DescriptorSize);
      if (Adler32.Compute(d.AsSpan(0, 60)) != BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(60)))
        throw new InvalidDataException($"EWF2: the section descriptor at {cursor} fails its Adler-32.");
      var type = BinaryPrimitives.ReadUInt32LittleEndian(d);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
      var previous = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8));
      var size = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(16));
      var padding = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(28));
      if (size > (ulong)(cursor - FileHeaderSize) || padding > size)
        throw new InvalidDataException($"EWF2: section at {cursor} claims more data than precedes it.");
      if ((flags & SectionEncrypted) != 0)
        throw new NotSupportedException("EWF2: the image is encrypted; EnCase's Ex01 encryption is not published.");
      if (found.Count == 0)
        lastType = type;
      found.Add(new Section(segment, type, flags, cursor - (long)size, (long)size, padding));
      if (previous == 0)
        break;
      if (previous >= (ulong)cursor)
        throw new InvalidDataException("EWF2: a section points forward to its predecessor.");
      cursor = (long)previous;
    }
    found.Reverse();
    foreach (var section in found) {
      this._sections.Add(section);
      switch (section.Type) {
        case TypeDeviceInformation when segment == 0:
          this.DeviceInformation = ParseObject(this.ReadData(section, withoutPadding: true));
          break;
        case TypeCaseData when segment == 0:
          this.CaseData = ParseObject(this.ReadData(section, withoutPadding: true));
          break;
        case TypeSectorTable:
          this.ReadTable(section);
          break;
        case TypeMd5:
          this.Md5 = this.ReadHash(section, 16);
          break;
        case TypeSha1:
          this.Sha1 = this.ReadHash(section, 20);
          break;
      }
    }
    return lastType;
  }

  private byte[] ReadData(Section section, bool withoutPadding = false)
    => ReadAt(this._segments[section.Segment], section.DataOffset, checked((int)(section.DataSize - (withoutPadding ? section.PaddingSize : 0))));

  private void ReadTable(Section section) {
    var data = this.ReadData(section);
    if (data.Length < 32)
      throw new InvalidDataException("EWF2: a sector table is shorter than its header.");
    if (Adler32.Compute(data.AsSpan(0, 16)) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)))
      throw new InvalidDataException("EWF2: a sector table header fails its Adler-32.");
    var firstChunk = BinaryPrimitives.ReadUInt64LittleEndian(data);
    var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
    if (count > (data.Length - 36) / 16)
      throw new InvalidDataException("EWF2: a sector table holds more entries than its section.");
    if (firstChunk != (ulong)this.Chunks.Count)
      throw new InvalidDataException($"EWF2: a sector table starts at chunk {firstChunk}; chunk {this.Chunks.Count} was next.");
    var entries = data.AsSpan(32, (int)count * 16);
    if (Adler32.Compute(entries) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32 + (int)count * 16)))
      throw new InvalidDataException("EWF2: the sector table entries fail their Adler-32.");
    for (var i = 0; i < count; ++i) {
      var e = entries.Slice(i * 16, 16);
      this.Chunks.Add(new Chunk(section.Segment, BinaryPrimitives.ReadUInt64LittleEndian(e),
        BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..])));
    }
  }

  private byte[] ReadHash(Section section, int length) {
    var data = this.ReadData(section);
    if (data.Length < length + 4 || Adler32.Compute(data.AsSpan(0, length)) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(length)))
      throw new InvalidDataException("EWF2: a hash section fails its Adler-32.");
    return data[..length];
  }

  /// <summary>Decodes chunk <paramref name="index"/> into <paramref name="destination"/> (the chunk's media bytes).</summary>
  internal void DecodeChunk(int index, Span<byte> destination) {
    var chunk = this.Chunks[index];
    var stream = this._segments[chunk.Segment];
    var chunkSize = this.BytesPerSector * this.SectorsPerChunk;
    if ((chunk.Flags & ChunkCompressed) != 0 && (chunk.Flags & ChunkPatternFill) != 0) {
      // The table entry's offset is the 64-bit fill pattern (size 0). libewf 20240506 instead
      // writes the 8 pattern bytes into the sector data and records their offset with size 8.
      Span<byte> pattern = stackalloc byte[8];
      if (chunk.Size == 0)
        BinaryPrimitives.WriteUInt64LittleEndian(pattern, chunk.Offset);
      else if (chunk.Size == 8)
        ReadAt(stream, checked((long)chunk.Offset), 8).CopyTo(pattern);
      else
        throw new InvalidDataException($"EWF2: pattern-filled chunk {index} has {chunk.Size} bytes.");
      for (var i = 0; i < destination.Length; ++i)
        destination[i] = pattern[i & 7];
      return;
    }
    if (chunk.Size > chunkSize + 4096L)
      throw new InvalidDataException($"EWF2: chunk {index} is {chunk.Size} bytes for a {chunkSize}-byte chunk.");
    var raw = ReadAt(stream, checked((long)chunk.Offset), (int)chunk.Size);
    byte[] data;
    if ((chunk.Flags & ChunkCompressed) != 0) {
      data = this.CompressionMethod switch {
        CompressionZlib => ZlibStream.Decompress(raw),
        CompressionBzip2 => Bunzip(raw),
        _ => throw new InvalidDataException($"EWF2: chunk {index} is compressed but the set's method is {this.CompressionMethod}."),
      };
    } else if ((chunk.Flags & ChunkChecksummed) != 0) {
      if (raw.Length < 4)
        throw new InvalidDataException($"EWF2: chunk {index} has no Adler-32 trailer.");
      data = raw[..^4];
      if (Adler32.Compute(data) != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(raw.Length - 4)))
        throw new InvalidDataException($"EWF2: chunk {index} fails its Adler-32.");
    } else
      data = raw;
    if (data.Length < destination.Length)
      throw new InvalidDataException($"EWF2: chunk {index} holds {data.Length} bytes; {destination.Length} were expected.");
    data.AsSpan(0, destination.Length).CopyTo(destination);
  }

  private static byte[] Bunzip(byte[] raw) {
    using var input = new MemoryStream(raw);
    using var bz = new Bzip2Stream(input, Compression.Core.Streams.CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    bz.CopyTo(output);
    return output.ToArray();
  }

  /// <summary>
  /// A serialized object string ("1", "main", tags, values; UTF-16LE with BOM, zlib-compressed):
  /// the tag line and the value line, tab-separated.
  /// </summary>
  internal static Dictionary<string, string> ParseObject(byte[] data) {
    byte[] text;
    try {
      text = ZlibStream.Decompress(data);
    } catch (InvalidDataException) {
      text = data;
    }
    var s = text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE
      ? Encoding.Unicode.GetString(text, 2, text.Length - 2)
      : Encoding.UTF8.GetString(text);
    var lines = s.Replace("\r", "").Split('\n');
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (lines.Length < 4)
      return result;
    var tags = lines[2].Split('\t');
    var values = lines[3].Split('\t');
    for (var i = 0; i < tags.Length; ++i)
      if (tags[i].Length > 0)
        result[tags[i]] = i < values.Length ? values[i] : "";
    return result;
  }

  private static int Number(IReadOnlyDictionary<string, string> values, string tag, int fallback)
    => values.TryGetValue(tag, out var text) && int.TryParse(text, out var n) ? n : fallback;

  /// <summary>Path of segment <paramref name="number"/> (1-based): .Ex01 ... .Ex99, .ExAA ... .ExZZ, .EyAA ...</summary>
  public static string SegmentPath(string firstPath, int number) {
    var extension = Path.GetExtension(firstPath);
    if (extension.Length != 5)
      throw new InvalidDataException($"EWF2: '{Path.GetFileName(firstPath)}' does not have a segment extension like .Ex01.");
    var prefix = extension.Substring(1, 2);
    string suffix;
    if (number <= 99)
      suffix = $"{prefix}{number:D2}";
    else {
      var m = number - 100;
      var upper = char.IsUpper(extension[3]) || !char.IsLetter(extension[3]);
      var a = upper ? 'A' : 'a';
      suffix = $"{prefix[0]}{(char)(prefix[1] + m / 676)}{(char)(a + m / 26 % 26)}{(char)(a + m % 26)}";
    }
    return Path.ChangeExtension(firstPath, "." + suffix);
  }

  internal static byte[] ReadAt(Stream stream, long offset, int length) {
    if (offset < 0 || length < 0 || offset > stream.Length - length)
      throw new InvalidDataException("EWF2: structure lies outside its segment file.");
    var buffer = new byte[length];
    stream.Position = offset;
    stream.ReadExactly(buffer);
    return buffer;
  }

  /// <summary>Releases the segment files this set opened.</summary>
  public void Dispose() {
    for (var i = 1; i < this._segments.Count; ++i)
      this._segments[i].Dispose();
    if (!this._leaveFirstOpen)
      this._segments[0].Dispose();
  }
}

/// <summary>Read-only access to the media of an <see cref="Ewf2Image"/>.</summary>
public sealed class Ewf2MediaStream : Stream {
  private readonly Ewf2Image _image;
  private readonly int _chunkSize;
  private readonly byte[] _cache;
  private int _cached = -1;
  private long _position;

  /// <summary>Wraps <paramref name="image"/>; disposing the stream disposes the image.</summary>
  public Ewf2MediaStream(Ewf2Image image) {
    this._image = image;
    this._chunkSize = image.BytesPerSector * image.SectorsPerChunk;
    this._cache = new byte[this._chunkSize];
  }

  /// <inheritdoc />
  public override bool CanRead => true;
  /// <inheritdoc />
  public override bool CanSeek => true;
  /// <inheritdoc />
  public override bool CanWrite => false;
  /// <inheritdoc />
  public override long Length => this._image.MediaSize;
  /// <inheritdoc />
  public override long Position {
    get => this._position;
    set => this._position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
  }

  /// <inheritdoc />
  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  /// <inheritdoc />
  public override int Read(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length && this._position < this.Length) {
      var index = (int)(this._position / this._chunkSize);
      var within = (int)(this._position % this._chunkSize);
      if (index != this._cached) {
        var length = (int)Math.Min(this._chunkSize, this.Length - (long)index * this._chunkSize);
        this._image.DecodeChunk(index, this._cache.AsSpan(0, length));
        this._cached = index;
      }
      var take = (int)Math.Min(Math.Min(buffer.Length - total, this._chunkSize - within), this.Length - this._position);
      this._cache.AsSpan(within, take).CopyTo(buffer[total..]);
      total += take;
      this._position += take;
    }
    return total;
  }

  /// <inheritdoc />
  public override long Seek(long offset, SeekOrigin origin) {
    this.Position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this.Length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    return this._position;
  }

  /// <inheritdoc />
  public override void Flush() { }
  /// <inheritdoc />
  public override void SetLength(long value) => throw new NotSupportedException("EWF2 media is read-only.");
  /// <inheritdoc />
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("EWF2 media is read-only.");

  /// <inheritdoc />
  protected override void Dispose(bool disposing) {
    if (disposing)
      this._image.Dispose();
    base.Dispose(disposing);
  }
}
