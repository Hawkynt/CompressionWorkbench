using System.Buffers.Binary;
using Compression.Core.Streams;
using FileFormat.Bzip2;

namespace FileFormat.Rzip;

/// <summary>
/// Reads and writes Andrew Tridgell's rzip 2.x files (<c>.rz</c>), as laid out in
/// <c>docs/RZIP-ON-DISK.md</c>: a 24-byte header, then chunks in which a control stream of
/// literal/match tokens and a literal stream are multiplexed in bzip2 (or stored) blocks.
/// </summary>
/// <remarks>
/// The encoder finds long-range matches with a polynomial rolling hash over the whole chunk,
/// after Andrew Tridgell, "Efficient Algorithms for Sorting and Synchronization" (PhD thesis,
/// ANU, 1999), chapter 5. Its settings affect only which valid tokens are written.
/// </remarks>
public static class RzipStream {

  /// <summary>A literal run, or a match of <see cref="Length"/> bytes <see cref="Distance"/> back.</summary>
  private readonly record struct Token(bool IsLiteral, long Distance, long Length);

  /// <summary>
  /// Compresses data into an rzip file using the default match-finder settings.
  /// </summary>
  /// <param name="input">The input stream containing data to compress.</param>
  /// <param name="output">The output stream to write the rzip file to.</param>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, RzipConstants.MinMatch, RzipConstants.CandidateSearchLimit);

  /// <summary>
  /// Compresses data into an rzip file using explicit encoder-only match-finder settings.
  /// </summary>
  internal static void Compress(Stream input, Stream output, int minMatch, int candidateSearchLimit) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfLessThan(minMatch, 2);
    ArgumentOutOfRangeException.ThrowIfLessThan(candidateSearchLimit, 1);

    byte[] data;
    using (var buffer = new MemoryStream()) {
      input.CopyTo(buffer);
      data = buffer.ToArray();
    }

    Span<byte> header = stackalloc byte[RzipConstants.HeaderSize];
    header.Clear();
    RzipConstants.Magic.CopyTo(header);
    header[4] = RzipConstants.VersionMajor;
    header[5] = RzipConstants.VersionMinor;
    BinaryPrimitives.WriteUInt32BigEndian(header[6..], (uint)data.LongLength);
    BinaryPrimitives.WriteUInt32BigEndian(header[10..], (uint)((ulong)data.LongLength >> 32));
    output.Write(header);

    for (var start = 0; start < data.Length; start += RzipConstants.ChunkSize) {
      var length = Math.Min(RzipConstants.ChunkSize, data.Length - start);
      WriteChunk(output, data.AsSpan(start, length), minMatch, candidateSearchLimit);
    }
  }

  private static void WriteChunk(Stream output, ReadOnlySpan<byte> chunk, int minMatch, int candidateSearchLimit) {
    var writer = new ChunkWriter();
    var position = 0;
    foreach (var token in Tokenize(chunk, minMatch, candidateSearchLimit)) {
      var remaining = token.Length;
      while (remaining > 0) {
        var n = (int)Math.Min(remaining, RzipConstants.MaxTokenLength);
        if (token.IsLiteral) {
          writer.Control(RzipConstants.TagLiteral, n);
          writer.Literals(chunk.Slice(position, n));
        } else {
          writer.Control(RzipConstants.TagMatch, n);
          writer.ControlUInt32((uint)token.Distance);
        }

        position += n;
        remaining -= n;
      }
    }

    writer.Control(RzipConstants.TagLiteral, 0);
    writer.ControlUInt32(Crc32(chunk));
    writer.WriteTo(output);
  }

  /// <summary>
  /// Decompresses an rzip file.
  /// </summary>
  /// <param name="input">The input stream containing the rzip file.</param>
  /// <param name="output">The output stream to write decompressed data to.</param>
  /// <exception cref="InvalidDataException">The stream is not a valid rzip file.</exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    byte[] data;
    using (var buffer = new MemoryStream()) {
      input.CopyTo(buffer);
      data = buffer.ToArray();
    }

    if (data.Length < RzipConstants.HeaderSize || !data.AsSpan(0, 4).SequenceEqual(RzipConstants.Magic))
      throw new InvalidDataException("Not an rzip file.");

    var expected = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(6)) | (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(10)) << 32;
    if (expected > Array.MaxLength)
      throw new InvalidDataException($"rzip original size {expected} exceeds the managed decoder limit.");

    var result = new byte[expected];
    var produced = 0;
    var chunkStart = RzipConstants.HeaderSize;
    while (produced < expected) {
      if (chunkStart >= data.Length)
        throw new InvalidDataException("rzip file ends before its original size was produced.");
      chunkStart = ReadChunk(data, chunkStart, result, ref produced);
    }

    output.Write(result);
  }

  /// <summary>Decodes one chunk starting at <paramref name="start"/>; returns where the next one starts.</summary>
  private static int ReadChunk(byte[] data, int start, byte[] result, ref int produced) {
    var reader = new ChunkReader(data, start);
    var chunkStart = produced;
    while (true) {
      var head = reader.ReadByte(0);
      var length = reader.ReadByte(0) | reader.ReadByte(0) << 8;
      if (head == 0 && length == 0)
        break;
      if (produced + length > result.Length)
        throw new InvalidDataException("rzip chunk produces more than the original size.");
      if (head == 0) {
        reader.Read(1, result.AsSpan(produced, length));
        produced += length;
        continue;
      }

      var distance = reader.ReadUInt32(0);
      if (distance == 0 || distance > produced)
        throw new InvalidDataException($"rzip match distance {distance} points outside the output.");
      var from = produced - (int)distance;
      for (var i = 0; i < length; ++i)
        result[produced + i] = result[from + i];
      produced += length;
    }

    var crc = reader.ReadUInt32(0);
    if (crc != Crc32(result.AsSpan(chunkStart, produced - chunkStart)))
      throw new InvalidDataException("rzip chunk CRC mismatch.");
    return reader.End;
  }

  /// <summary>
  /// Splits the chunk into literal runs and long-range matches. Each hash value keeps enough
  /// recent positions to satisfy <paramref name="candidateSearchLimit"/>; the newest are
  /// examined first and a candidate must be strictly longer to win, so among equally long
  /// matches the nearest is taken. Positions covered by an emitted match are not indexed.
  /// </summary>
  private static List<Token> Tokenize(ReadOnlySpan<byte> data, int minMatch, int candidateSearchLimit) {
    var tokens = new List<Token>();
    var length = data.Length;
    if (length == 0)
      return tokens;

    if (length < minMatch) {
      tokens.Add(new(true, 0, length));
      return tokens;
    }

    var basePower = 1u;
    for (var i = 0; i < minMatch - 1; ++i)
      basePower = unchecked(basePower * RzipConstants.RollingHashBase);

    var buckets = new Dictionary<uint, List<int>>();
    var bucketCapacity = Math.Max(RzipConstants.HashBucketCapacity, candidateSearchLimit);
    var lastPosition = length - minMatch;
    var literalStart = 0;
    var hash = HashAt(data, 0, minMatch);
    var hashValid = true;

    for (var position = 0; position <= lastPosition;) {
      if (!hashValid) {
        hash = HashAt(data, position, minMatch);
        hashValid = true;
      }

      var bestLength = 0;
      var bestPosition = -1;

      if (buckets.TryGetValue(hash, out var bucket)) {
        var stop = Math.Max(0, bucket.Count - candidateSearchLimit);
        for (var index = bucket.Count - 1; index >= stop; --index) {
          var candidate = bucket[index];
          var limit = length - position;
          var matched = 0;
          while (matched < limit && data[candidate + matched] == data[position + matched])
            ++matched;

          if (matched < minMatch || matched <= bestLength)
            continue;

          bestLength = matched;
          bestPosition = candidate;
        }
      } else
        buckets[hash] = bucket = [];

      bucket.Add(position);
      if (bucket.Count > bucketCapacity)
        bucket.RemoveAt(0);

      if (bestLength >= minMatch) {
        if (position > literalStart)
          tokens.Add(new(true, 0, position - literalStart));

        tokens.Add(new(false, position - bestPosition, bestLength));
        position += bestLength;
        literalStart = position;
        hashValid = false;
        continue;
      }

      if (position < lastPosition)
        hash = unchecked((hash - (uint)data[position] * basePower) * RzipConstants.RollingHashBase
          + data[position + minMatch]);

      ++position;
    }

    if (literalStart < length)
      tokens.Add(new(true, 0, length - literalStart));

    return tokens;
  }

  private static uint HashAt(ReadOnlySpan<byte> data, int position, int windowLength) {
    var hash = 0u;
    for (var i = 0; i < windowLength; ++i)
      hash = unchecked(hash * RzipConstants.RollingHashBase + data[position + i]);

    return hash;
  }

  /// <summary>CRC-32 (reflected polynomial 0xEDB88320) with initial value 0 and no final XOR, as rzip keeps it.</summary>
  private static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0u;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; ++i)
        crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
    }

    return crc;
  }

  /// <summary>Buffers the two streams of a chunk and lays out their linked blocks.</summary>
  private sealed class ChunkWriter {
    private readonly MemoryStream _chunk = new();
    private readonly MemoryStream[] _buffers = [new(), new()];
    private readonly long[] _lastHead = new long[RzipConstants.StreamCount];

    public ChunkWriter() {
      for (var s = 0; s < RzipConstants.StreamCount; ++s) {
        this._lastHead[s] = this._chunk.Position + 9;
        this.WriteBlockHeader(RzipConstants.BlockStored, 0, 0);
      }
    }

    public void Control(byte head, int length) {
      this.Append(0, [head, (byte)length, (byte)(length >> 8)]);
    }

    public void ControlUInt32(uint value) {
      Span<byte> bytes = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
      this.Append(0, bytes);
    }

    public void Literals(ReadOnlySpan<byte> bytes) => this.Append(1, bytes);

    private void Append(int stream, ReadOnlySpan<byte> bytes) {
      var buffer = this._buffers[stream];
      while (!bytes.IsEmpty) {
        var n = (int)Math.Min(RzipConstants.StreamBufferSize - buffer.Length, bytes.Length);
        buffer.Write(bytes[..n]);
        bytes = bytes[n..];
        if (buffer.Length == RzipConstants.StreamBufferSize)
          this.Flush(stream);
      }
    }

    private void Flush(int stream) {
      var buffer = this._buffers[stream];
      var raw = buffer.ToArray();
      buffer.SetLength(0);

      var blockStart = this._chunk.Length;
      this._chunk.Position = this._lastHead[stream];
      Span<byte> link = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(link, (uint)blockStart);
      this._chunk.Write(link);
      this._chunk.Position = blockStart;
      this._lastHead[stream] = blockStart + 9;

      var packed = Bzip2(raw);
      if (packed.Length < raw.Length) {
        this.WriteBlockHeader(RzipConstants.BlockBzip2, packed.Length, raw.Length);
        this._chunk.Write(packed);
      } else {
        this.WriteBlockHeader(RzipConstants.BlockStored, raw.Length, raw.Length);
        this._chunk.Write(raw);
      }
    }

    private void WriteBlockHeader(byte type, int compressedLength, int uncompressedLength) {
      Span<byte> header = stackalloc byte[RzipConstants.BlockHeaderSize];
      header.Clear();
      header[0] = type;
      BinaryPrimitives.WriteUInt32LittleEndian(header[1..], (uint)compressedLength);
      BinaryPrimitives.WriteUInt32LittleEndian(header[5..], (uint)uncompressedLength);
      this._chunk.Write(header);
    }

    public void WriteTo(Stream output) {
      for (var s = 0; s < RzipConstants.StreamCount; ++s)
        if (this._buffers[s].Length > 0)
          this.Flush(s);
      this._chunk.Position = 0;
      this._chunk.CopyTo(output);
    }

    private static byte[] Bzip2(byte[] data) {
      using var packed = new MemoryStream();
      using (var bzip2 = new Bzip2Stream(packed, CompressionStreamMode.Compress, blockSize100k: RzipConstants.Bzip2Level, leaveOpen: true))
        bzip2.Write(data, 0, data.Length);
      return packed.ToArray();
    }
  }

  /// <summary>Follows the linked blocks of a chunk's two streams.</summary>
  private sealed class ChunkReader {
    private readonly byte[] _data;
    private readonly int _start;
    private readonly long[] _nextHead = new long[RzipConstants.StreamCount];
    private readonly byte[][] _buffers = [[], []];
    private readonly int[] _positions = new int[RzipConstants.StreamCount];
    private long _totalRead;

    public ChunkReader(byte[] data, int start) {
      this._data = data;
      while (true) {
        var first = this.Header(start);
        if (first is (RzipConstants.BlockStored, 0, 0, 0)) {
          // rzip's stream close workaround: an all-zero leading header is skipped.
          start += RzipConstants.BlockHeaderSize;
          continue;
        }

        break;
      }

      this._start = start;
      for (var s = 0; s < RzipConstants.StreamCount; ++s) {
        var header = this.Header(start + s * RzipConstants.BlockHeaderSize);
        if (header.Type != RzipConstants.BlockStored || header.CompressedLength != 0 || header.UncompressedLength != 0)
          throw new InvalidDataException("rzip chunk does not start with empty stream headers.");
        this._nextHead[s] = header.Next;
        this._totalRead += RzipConstants.BlockHeaderSize;
      }
    }

    /// <summary>Where the next chunk starts: after every header and block this chunk read.</summary>
    public int End => checked((int)(this._start + this._totalRead));

    public byte ReadByte(int stream) {
      this.Ensure(stream);
      return this._buffers[stream][this._positions[stream]++];
    }

    public uint ReadUInt32(int stream)
      => (uint)(this.ReadByte(stream) | this.ReadByte(stream) << 8 | this.ReadByte(stream) << 16 | this.ReadByte(stream) << 24);

    public void Read(int stream, Span<byte> destination) {
      while (!destination.IsEmpty) {
        this.Ensure(stream);
        var available = this._buffers[stream].AsSpan(this._positions[stream]);
        var n = Math.Min(available.Length, destination.Length);
        available[..n].CopyTo(destination);
        destination = destination[n..];
        this._positions[stream] += n;
      }
    }

    private void Ensure(int stream) {
      while (this._positions[stream] == this._buffers[stream].Length) {
        if (this._nextHead[stream] == 0)
          throw new InvalidDataException($"rzip stream {stream} ends early.");
        var at = this._start + this._nextHead[stream];
        var header = this.Header(at);
        this._nextHead[stream] = header.Next;
        var dataStart = at + RzipConstants.BlockHeaderSize;
        if (dataStart + header.CompressedLength > this._data.Length)
          throw new InvalidDataException("rzip block runs past the end of the file.");
        var block = this._data.AsSpan((int)dataStart, (int)header.CompressedLength);
        this._totalRead += RzipConstants.BlockHeaderSize + header.CompressedLength;
        this._buffers[stream] = header.Type switch {
          RzipConstants.BlockStored when header.CompressedLength == header.UncompressedLength => block.ToArray(),
          RzipConstants.BlockBzip2 => Bunzip2(block, header.UncompressedLength),
          _ => throw new InvalidDataException($"rzip block type {header.Type} with lengths {header.CompressedLength}/{header.UncompressedLength} is not valid."),
        };
        this._positions[stream] = 0;
      }
    }

    private (byte Type, long CompressedLength, long UncompressedLength, long Next) Header(long at) {
      if (at < 0 || at + RzipConstants.BlockHeaderSize > this._data.Length)
        throw new InvalidDataException("rzip block header lies outside the file.");
      var span = this._data.AsSpan((int)at, RzipConstants.BlockHeaderSize);
      return (span[0], BinaryPrimitives.ReadUInt32LittleEndian(span[1..]), BinaryPrimitives.ReadUInt32LittleEndian(span[5..]),
        BinaryPrimitives.ReadUInt32LittleEndian(span[9..]));
    }

    private static byte[] Bunzip2(ReadOnlySpan<byte> packed, long expected) {
      using var input = new MemoryStream(packed.ToArray());
      using var bzip2 = new Bzip2Stream(input, CompressionStreamMode.Decompress, leaveOpen: true);
      using var output = new MemoryStream();
      bzip2.CopyTo(output);
      if (output.Length != expected)
        throw new InvalidDataException($"rzip bzip2 block holds {output.Length} bytes, its header says {expected}.");
      return output.ToArray();
    }
  }
}
