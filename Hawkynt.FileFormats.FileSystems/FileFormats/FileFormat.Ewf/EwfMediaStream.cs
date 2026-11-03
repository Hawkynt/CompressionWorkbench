#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using FileFormat.Zlib;

namespace FileFormat.Ewf;

/// <summary>
/// Read-only, seekable view of the media stored in an EWF-E01 segment file set (.E01, .E02, …,
/// .E99, .EAA, …). Every segment's section chain is walked once; every "table" section adds its
/// chunks (falling back to its "table2" mirror when the table's checksum fails), and chunks are
/// decoded on demand: zlib when the entry's top bit is set, otherwise stored data followed by its
/// Adler-32.
/// </summary>
/// <remarks>
/// Layout per Joachim Metz's "Expert Witness Compression Format (EWF)" (libewf documentation):
/// FTK Imager / EnCase 2–5 tables hold file-relative chunk offsets, EnCase 6–7 tables add a base
/// offset; a table's last chunk ends where its "sectors" section ends. Further segments are found
/// next to the first one by name, so a multi-segment set must be opened from a file.
/// </remarks>
public sealed class EwfMediaStream : Stream {
  private const int FileHeaderSize = 13;
  private const int DescriptorSize = 76;
  private const int MaxSegments = 14_971; // E01..E99 plus EAA..ZZZ

  private readonly List<Stream> _segments;
  private readonly bool _leaveFirstOpen;
  private readonly List<Chunk> _chunks = [];
  private readonly int _chunkSize;
  private readonly long _length;
  private long _position;
  private int _cachedChunk = -1;
  private byte[] _cache = [];

  private readonly record struct Chunk(int Segment, long Offset, long Length, bool Compressed);

  /// <summary>Number of segment files the set consists of.</summary>
  public int SegmentCount => _segments.Count;

  /// <summary>Bytes per chunk (sectors per chunk times bytes per sector).</summary>
  public int ChunkSize => _chunkSize;

  private EwfMediaStream(List<Stream> segments, bool leaveFirstOpen) {
    _segments = segments;
    _leaveFirstOpen = leaveFirstOpen;
    long sectorCount = -1;
    var bytesPerSector = 0;
    var sectorsPerChunk = 0;

    for (var s = 0; s < segments.Count; ++s) {
      var segment = segments[s];
      var header = ReadAt(segment, 0, FileHeaderSize);
      if (!header.AsSpan(0, 8).SequenceEqual(EwfReader.EvfSignature))
        throw new InvalidDataException($"EWF: segment {s + 1} is not an EVF (E01) segment file.");
      var number = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(9));
      if (number != s + 1)
        throw new InvalidDataException($"EWF: segment file {s + 1} declares segment number {number}.");

      long cursor = FileHeaderSize;
      long sectorsEnd = -1;
      var pendingTable2 = false;
      var guard = 0;
      while (true) {
        if (++guard > 1_000_000)
          throw new InvalidDataException("EWF: section chain does not end.");
        if (cursor + DescriptorSize > segment.Length)
          throw new InvalidDataException($"EWF: segment {s + 1} ends inside its section chain.");
        var descriptor = ReadAt(segment, cursor, DescriptorSize);
        var type = Encoding.ASCII.GetString(descriptor, 0, 16).TrimEnd('\0');
        var next = (long)BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(16));
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(24));
        if (Adler32.Compute(descriptor.AsSpan(0, 72)) != BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(72)))
          throw new InvalidDataException($"EWF: section descriptor '{type}' in segment {s + 1} fails its checksum.");

        switch (type) {
          case "volume" or "disk" when s == 0: {
            var volume = ReadAt(segment, cursor + DescriptorSize, 24);
            sectorsPerChunk = (int)BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(8));
            bytesPerSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(12));
            sectorCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(volume.AsSpan(16));
            break;
          }
          case "sectors":
            sectorsEnd = checked(cursor + size);
            break;
          case "table":
            pendingTable2 = !AddTable(segment, s, cursor + DescriptorSize, size - DescriptorSize, sectorsEnd);
            break;
          case "table2":
            if (pendingTable2 && !AddTable(segment, s, cursor + DescriptorSize, size - DescriptorSize, sectorsEnd))
              throw new InvalidDataException($"EWF: both copies of a chunk table in segment {s + 1} fail their checksums.");
            pendingTable2 = false;
            break;
        }

        if (type is "done" or "next") {
          if (type == "done" && s != segments.Count - 1)
            throw new InvalidDataException("EWF: the set ends ('done') before its last segment file.");
          if (type == "next" && s == segments.Count - 1)
            throw new InvalidDataException($"EWF: segment {s + 1} continues ('next') but no further segment file was found.");
          break;
        }
        if (pendingTable2 && type != "table")
          throw new InvalidDataException($"EWF: a chunk table in segment {s + 1} fails its checksum and has no table2 mirror.");
        if (next <= cursor || next > segment.Length)
          throw new InvalidDataException($"EWF: section '{type}' in segment {s + 1} points to an invalid next section.");
        cursor = next;
      }
    }

    if (sectorCount < 0 || bytesPerSector <= 0 || sectorsPerChunk <= 0)
      throw new InvalidDataException("EWF: no volume section describes the media geometry.");
    _chunkSize = checked(sectorsPerChunk * bytesPerSector);
    _length = checked(sectorCount * bytesPerSector);
    if ((long)_chunks.Count * _chunkSize < _length)
      throw new InvalidDataException($"EWF: {_chunks.Count} chunks cannot hold {_length} bytes of media.");
  }

  /// <summary>Adds a table's chunks; false when its checksums fail (the caller then tries table2).</summary>
  private bool AddTable(Stream segment, int segmentIndex, long payloadOffset, long payloadLength, long sectorsEnd) {
    if (payloadLength < 24)
      return false;
    var header = ReadAt(segment, payloadOffset, 24);
    if (Adler32.Compute(header.AsSpan(0, 20)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)))
      return false;
    var count = BinaryPrimitives.ReadUInt32LittleEndian(header);
    var baseOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
    if (count > 1_000_000 || 24 + count * 4L > payloadLength)
      return false;
    var entries = ReadAt(segment, payloadOffset + 24, checked((int)(count * 4)));
    if (24 + count * 4L + 4 <= payloadLength) {
      var footer = ReadAt(segment, payloadOffset + 24 + count * 4L, 4);
      if (Adler32.Compute(entries) != BinaryPrimitives.ReadUInt32LittleEndian(footer))
        return false;
    }
    if (sectorsEnd < 0)
      throw new InvalidDataException($"EWF: a chunk table in segment {segmentIndex + 1} has no sectors section before it.");

    var added = new List<Chunk>((int)count);
    for (var i = 0; i < count; ++i) {
      var raw = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 4));
      var start = checked(baseOffset + (raw & 0x7FFFFFFF));
      var end = i + 1 < count
        ? checked(baseOffset + (BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan((i + 1) * 4)) & 0x7FFFFFFF))
        : sectorsEnd;
      if (start < FileHeaderSize || end < start || end > segment.Length)
        throw new InvalidDataException($"EWF: chunk {_chunks.Count + i} lies outside its segment file.");
      added.Add(new Chunk(segmentIndex, start, end - start, (raw & 0x80000000) != 0));
    }
    _chunks.AddRange(added);
    return true;
  }

  /// <summary>
  /// Opens the media of the segment set <paramref name="first"/> starts. When the first segment
  /// continues into others, they are looked up next to it by EWF's naming (E02 … E99, EAA …).
  /// </summary>
  /// <exception cref="InvalidDataException">The set is malformed, damaged or incomplete.</exception>
  /// <exception cref="NotSupportedException">The set has further segments but was not opened from a file.</exception>
  public static EwfMediaStream Open(Stream first, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(first);
    var segments = new List<Stream> { first };
    try {
      while (EndsWithNext(segments[^1])) {
        if (first is not FileStream file)
          throw new NotSupportedException("EWF: the image continues in further segment files; open it from a file path so they can be found.");
        if (segments.Count >= MaxSegments)
          throw new InvalidDataException("EWF: the segment set has too many files.");
        var path = SegmentPath(file.Name, segments.Count + 1);
        if (!File.Exists(path))
          throw new InvalidDataException($"EWF: segment file '{Path.GetFileName(path)}' is missing.");
        segments.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
      }
      return new EwfMediaStream(segments, leaveOpen);
    } catch {
      for (var i = 1; i < segments.Count; ++i)
        segments[i].Dispose();
      throw;
    }
  }

  /// <summary>Path of segment <paramref name="number"/> (1-based) of the set whose first file is <paramref name="firstPath"/>.</summary>
  public static string SegmentPath(string firstPath, int number) {
    var extension = Path.GetExtension(firstPath);
    if (extension.Length != 4)
      throw new InvalidDataException($"EWF: '{Path.GetFileName(firstPath)}' does not have a segment extension like .E01.");
    var letter = extension[1];
    var lower = char.IsLower(letter);
    string suffix;
    if (number <= 99) {
      suffix = $"{char.ToUpperInvariant(letter)}{number:D2}";
    } else {
      var m = number - 100;
      suffix = $"{(char)(char.ToUpperInvariant(letter) + m / 676)}{(char)('A' + m / 26 % 26)}{(char)('A' + m % 26)}";
    }
    return Path.ChangeExtension(firstPath, "." + (lower ? suffix.ToLowerInvariant() : suffix));
  }

  /// <summary>Whether the segment's section chain ends in "next" (more segments follow).</summary>
  internal static bool EndsWithNext(Stream segment) {
    long cursor = FileHeaderSize;
    for (var guard = 0; guard < 1_000_000 && cursor + DescriptorSize <= segment.Length; ++guard) {
      var descriptor = ReadAt(segment, cursor, DescriptorSize);
      var type = Encoding.ASCII.GetString(descriptor, 0, 16).TrimEnd('\0');
      if (type == "next")
        return true;
      if (type == "done")
        return false;
      var next = (long)BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(16));
      if (next <= cursor)
        return false;
      cursor = next;
    }
    return false;
  }

  private static byte[] ReadAt(Stream stream, long offset, int length) {
    if (offset < 0 || length < 0 || offset > stream.Length - length)
      throw new InvalidDataException("EWF: structure lies outside its segment file.");
    var buffer = new byte[length];
    stream.Position = offset;
    stream.ReadExactly(buffer);
    return buffer;
  }

  private byte[] DecodeChunk(int index) {
    if (index == _cachedChunk)
      return _cache;
    var chunk = _chunks[index];
    if (chunk.Length > _chunkSize + 4096L)
      throw new InvalidDataException($"EWF: chunk {index} is {chunk.Length} bytes for a {_chunkSize}-byte chunk.");
    var raw = ReadAt(_segments[chunk.Segment], chunk.Offset, (int)chunk.Length);
    var expected = (int)Math.Min(_chunkSize, _length - (long)index * _chunkSize);
    byte[] data;
    if (chunk.Compressed) {
      data = ZlibStream.Decompress(raw);
    } else {
      if (raw.Length < 4)
        throw new InvalidDataException($"EWF: stored chunk {index} has no Adler-32 trailer.");
      data = raw[..^4];
      if (Adler32.Compute(data) != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(raw.Length - 4)))
        throw new InvalidDataException($"EWF: stored chunk {index} fails its Adler-32.");
    }
    if (data.Length < expected)
      throw new InvalidDataException($"EWF: chunk {index} holds {data.Length} bytes; {expected} were expected.");
    _cache = data;
    _cachedChunk = index;
    return data;
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => _length;

  public override long Position {
    get => _position;
    set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
  }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length && _position < _length) {
      var index = (int)(_position / _chunkSize);
      var within = (int)(_position % _chunkSize);
      var data = DecodeChunk(index);
      var take = (int)Math.Min(Math.Min(buffer.Length - total, _chunkSize - within), _length - _position);
      data.AsSpan(within, take).CopyTo(buffer[total..]);
      total += take;
      _position += take;
    }
    return total;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var target = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => _position + offset,
      SeekOrigin.End => _length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    Position = target;
    return target;
  }

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException("EWF media is read-only.");
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("EWF media is read-only.");

  protected override void Dispose(bool disposing) {
    if (disposing) {
      for (var i = 1; i < _segments.Count; ++i)
        _segments[i].Dispose();
      if (!_leaveFirstOpen)
        _segments[0].Dispose();
    }
    base.Dispose(disposing);
  }
}
