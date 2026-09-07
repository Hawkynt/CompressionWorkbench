#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Lizard;

/// <summary>
/// Managed Lizard (formerly LZ5) frame codec for the interoperable fast-LZ4
/// codeword family used by compression levels 10 through 19.
/// </summary>
/// <remarks>
/// This is a clean-room implementation from the published Lizard block/frame
/// format. Lizard's so-called LZ4 codewords are carried in Lizard's five-stream
/// block layout and are not ordinary LZ4 blocks. LIZv1 and Huffman-coded levels
/// 20 through 49 are intentionally not emitted by this managed subset.
/// </remarks>
public static class LizardStream {

  private static readonly byte[] Magic = [0x06, 0x22, 0x4D, 0x18];
  private const byte DefaultFlags = 0x68; // version=01, independent blocks, content size present
  private const int RawBlockSize = 128 * 1024;
  private const int MinMatch = 4;
  private const int LastLiterals = 16;
  private const int MatchFindLimit = LastLiterals + MinMatch;
  private const int MaxOffset = ushort.MaxValue;
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int DefaultCompressionLevel = 17;
  private const int DefaultFrameBlockSize = 4 * 1024 * 1024;

  private static readonly IReadOnlyDictionary<int, byte> BlockSizeIds = new Dictionary<int, byte> {
    [128 * 1024] = 1,
    [256 * 1024] = 2,
    [1024 * 1024] = 3,
    [4 * 1024 * 1024] = 4,
    [16 * 1024 * 1024] = 5,
    [64 * 1024 * 1024] = 6,
    [256 * 1024 * 1024] = 7,
  };

  /// <summary>Compresses input using Lizard level 17 and 4 MiB independent frame blocks.</summary>
  public static void Compress(Stream input, Stream output) =>
    Compress(input, output, DefaultCompressionLevel, DefaultFrameBlockSize);

  /// <summary>Compresses input using a Lizard fast-LZ4 level (10-19) and selected frame block size.</summary>
  public static void Compress(Stream input, Stream output, int compressionLevel, int blockSize) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ValidateCompressionLevel(compressionLevel);
    if (!BlockSizeIds.TryGetValue(blockSize, out var blockSizeId))
      throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Unsupported Lizard frame block size.");

    using var source = new MemoryStream();
    input.CopyTo(source);
    var data = source.ToArray();

    output.Write(Magic);
    output.WriteByte(DefaultFlags);
    output.WriteByte((byte)(blockSizeId << 4));

    Span<byte> contentSize = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(contentSize, (ulong)data.LongLength);
    output.Write(contentSize);

    Span<byte> descriptor = stackalloc byte[10];
    descriptor[0] = DefaultFlags;
    descriptor[1] = (byte)(blockSizeId << 4);
    contentSize.CopyTo(descriptor[2..]);
    output.WriteByte((byte)(XxHash32(descriptor) >> 8));

    Span<byte> sizeBuffer = stackalloc byte[4];
    for (var offset = 0; offset < data.Length;) {
      var count = Math.Min(blockSize, data.Length - offset);
      var sourceBlock = data.AsSpan(offset, count);
      var compressed = CompressDataBlock(sourceBlock, compressionLevel);

      if (compressed.Length >= count) {
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuffer, (uint)count | 0x80000000u);
        output.Write(sizeBuffer);
        output.Write(sourceBlock);
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuffer, (uint)compressed.Length);
        output.Write(sizeBuffer);
        output.Write(compressed);
      }

      offset += count;
    }

    BinaryPrimitives.WriteUInt32LittleEndian(sizeBuffer, 0);
    output.Write(sizeBuffer);
  }

  /// <summary>Decompresses one Lizard frame. Levels 10-19 are supported for compressed blocks.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> magic = stackalloc byte[4];
    input.ReadExactly(magic);
    if (!magic.SequenceEqual(Magic))
      throw new InvalidDataException("Not a Lizard stream: invalid magic.");

    var flags = ReadByte(input, "frame flags");
    if ((flags >> 6) != 1)
      throw new InvalidDataException($"Unsupported Lizard frame version {(flags >> 6)}.");
    if ((flags & 0x03) != 0)
      throw new InvalidDataException("Lizard frame has non-zero reserved flag bits.");

    var blockIndependent = (flags & 0x20) != 0;
    var blockChecksum = (flags & 0x10) != 0;
    var hasContentSize = (flags & 0x08) != 0;
    var contentChecksum = (flags & 0x04) != 0;

    var blockDescriptor = ReadByte(input, "block descriptor");
    if ((blockDescriptor & 0x8F) != 0)
      throw new InvalidDataException("Lizard frame has non-zero reserved block descriptor bits.");
    var blockSizeId = (blockDescriptor >> 4) & 7;
    var maxFrameBlockSize = GetBlockSize(blockSizeId);

    Span<byte> descriptor = stackalloc byte[10];
    var descriptorLength = 2;
    descriptor[0] = flags;
    descriptor[1] = blockDescriptor;

    ulong? declaredContentSize = null;
    if (hasContentSize) {
      Span<byte> contentSize = stackalloc byte[8];
      input.ReadExactly(contentSize);
      declaredContentSize = BinaryPrimitives.ReadUInt64LittleEndian(contentSize);
      contentSize.CopyTo(descriptor[2..]);
      descriptorLength += 8;
    }

    var expectedHeaderChecksum = ReadByte(input, "header checksum");
    var actualHeaderChecksum = (byte)(XxHash32(descriptor[..descriptorLength]) >> 8);
    if (expectedHeaderChecksum != actualHeaderChecksum)
      throw new InvalidDataException("Lizard frame header checksum mismatch.");

    var history = new List<byte>();
    using var decodedFrame = new MemoryStream();
    Span<byte> blockSizeBuffer = stackalloc byte[4];
    Span<byte> checksumBuffer = stackalloc byte[4];

    while (true) {
      input.ReadExactly(blockSizeBuffer);
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(blockSizeBuffer);
      if (rawSize == 0)
        break;

      var stored = (rawSize & 0x80000000u) != 0;
      var byteCount = checked((int)(rawSize & 0x7FFFFFFFu));
      if (byteCount > maxFrameBlockSize)
        throw new InvalidDataException($"Lizard frame block size {byteCount} exceeds advertised maximum {maxFrameBlockSize}.");

      var block = new byte[byteCount];
      input.ReadExactly(block);

      if (blockChecksum) {
        input.ReadExactly(checksumBuffer);
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(checksumBuffer);
        if (XxHash32(block) != expected)
          throw new InvalidDataException("Lizard block checksum mismatch.");
      }

      if (blockIndependent)
        history.Clear();

      var blockOutputStart = history.Count;
      if (stored) {
        history.AddRange(block);
      } else {
        DecompressDataBlock(block, history);
      }

      var decodedBlockSize = history.Count - blockOutputStart;
      if (decodedBlockSize > maxFrameBlockSize)
        throw new InvalidDataException("Decoded Lizard block exceeds the frame's advertised maximum block size.");

      if (decodedBlockSize > 0) {
        var decodedBlock = history.GetRange(blockOutputStart, decodedBlockSize).ToArray();
        decodedFrame.Write(decodedBlock);
      }
    }

    var decoded = decodedFrame.ToArray();
    if (contentChecksum) {
      input.ReadExactly(checksumBuffer);
      var expected = BinaryPrimitives.ReadUInt32LittleEndian(checksumBuffer);
      if (XxHash32(decoded) != expected)
        throw new InvalidDataException("Lizard content checksum mismatch.");
    }

    if (declaredContentSize is { } size && size != (ulong)decoded.LongLength)
      throw new InvalidDataException($"Lizard content size mismatch: header says {size}, decoded {decoded.LongLength}.");

    output.Write(decoded);
  }

  private static byte[] CompressDataBlock(ReadOnlySpan<byte> source, int compressionLevel) {
    using var output = new MemoryStream();
    output.WriteByte((byte)compressionLevel);

    for (var offset = 0; offset < source.Length; offset += RawBlockSize) {
      var count = Math.Min(RawBlockSize, source.Length - offset);
      var block = source.Slice(offset, count);
      var compressed = CompressRawBlock(block, compressionLevel);

      if (compressed.Length == 0 || compressed.Length >= count + 4) {
        output.WriteByte(0x80);
        WriteUInt24(output, count);
        output.Write(block);
      } else {
        output.Write(compressed);
      }
    }

    return output.ToArray();
  }

  private static byte[] CompressRawBlock(ReadOnlySpan<byte> source, int compressionLevel) {
    if (source.Length < MatchFindLimit)
      return [];

    var data = source.ToArray();
    var heads = new int[HashSize];
    var previous = new int[data.Length];
    Array.Fill(heads, -1);
    Array.Fill(previous, -1);

    using var flags = new MemoryStream();
    using var literals = new MemoryStream();

    var anchor = 0;
    var position = 0;
    var matchStartLimit = data.Length - MatchFindLimit;
    var searchDepth = GetSearchDepth(compressionLevel);
    var lazy = compressionLevel >= 18;

    while (position <= matchStartLimit) {
      var match = FindBestMatch(data, position, heads, previous, searchDepth);
      if (match.Length < MinMatch) {
        Insert(data, position, heads, previous);
        ++position;
        continue;
      }

      if (lazy && position < matchStartLimit) {
        Insert(data, position, heads, previous);
        var next = FindBestMatch(data, position + 1, heads, previous, searchDepth);
        if (next.Length > match.Length + 1) {
          ++position;
          continue;
        }
      }

      EmitSequence(flags, literals, data, anchor, position, match.Offset, match.Length);
      var end = position + match.Length;
      for (var p = lazy ? position + 1 : position; p < end && p <= matchStartLimit; ++p)
        Insert(data, p, heads, previous);

      position = end;
      anchor = position;
    }

    literals.Write(data.AsSpan(anchor));
    if (flags.Length == 0)
      return [];

    using var output = new MemoryStream();
    output.WriteByte(0); // no Huffman streams for levels 10-19
    WriteStream(output, ReadOnlySpan<byte>.Empty); // lengths stream
    WriteStream(output, ReadOnlySpan<byte>.Empty); // 16-bit offset stream; embedded in literals for this codeword family
    WriteStream(output, ReadOnlySpan<byte>.Empty); // 24-bit offset stream
    WriteStream(output, flags.ToArray());
    WriteStream(output, literals.ToArray());
    return output.ToArray();
  }

  private static void DecompressDataBlock(ReadOnlySpan<byte> source, List<byte> output) {
    if (source.IsEmpty)
      throw new InvalidDataException("Lizard compressed block is empty.");

    var compressionLevel = source[0];
    ValidateCompressionLevel(compressionLevel);
    var position = 1;

    while (position < source.Length) {
      var blockOutputStart = output.Count;
      var header = source[position++];
      if (header == 0x80) {
        var length = ReadUInt24(source, ref position);
        if (length > RawBlockSize || length > source.Length - position)
          throw new InvalidDataException("Invalid uncompressed Lizard block length.");
        AddBytes(output, source.Slice(position, length));
        position += length;
        continue;
      }

      if ((header & 0x80) != 0)
        throw new InvalidDataException($"Unknown Lizard internal block header 0x{header:X2}.");
      if ((header & 0x1F) != 0)
        throw new NotSupportedException("This managed Lizard decoder supports levels 10-19 without Huffman/LIZv1 entropy streams.");

      var lengths = ReadStream(source, ref position, "lengths");
      var offsets16 = ReadStream(source, ref position, "16-bit offsets");
      var offsets24 = ReadStream(source, ref position, "24-bit offsets");
      var tokens = ReadStream(source, ref position, "tokens");
      var literals = ReadStream(source, ref position, "literals");

      if (!lengths.IsEmpty || !offsets16.IsEmpty || !offsets24.IsEmpty)
        throw new InvalidDataException("Lizard levels 10-19 must not use separate length or offset streams.");

      var literalPosition = 0;
      foreach (var token in tokens) {
        var literalLength = token & 0x0F;
        if (literalLength == 15)
          literalLength += ReadExtendedLength(literals, ref literalPosition);

        EnsureRawBlockCapacity(output.Count - blockOutputStart, literalLength);
        if (literalLength > literals.Length - literalPosition)
          throw new InvalidDataException("Lizard literal run exceeds the literals stream.");
        AddBytes(output, literals.Slice(literalPosition, literalLength));
        literalPosition += literalLength;

        if (literals.Length - literalPosition < 2)
          throw new InvalidDataException("Lizard match is missing its 16-bit offset.");
        var matchOffset = BinaryPrimitives.ReadUInt16LittleEndian(literals[literalPosition..]);
        literalPosition += 2;
        if (matchOffset == 0 || matchOffset > output.Count)
          throw new InvalidDataException($"Lizard match offset {matchOffset} is invalid at output position {output.Count}.");

        var matchLength = token >> 4;
        if (matchLength == 15)
          matchLength += ReadExtendedLength(literals, ref literalPosition);
        matchLength += MinMatch;

        EnsureRawBlockCapacity(output.Count - blockOutputStart, matchLength);
        var matchStart = output.Count - matchOffset;
        for (var i = 0; i < matchLength; ++i)
          output.Add(output[matchStart + i]);
      }

      var finalLiterals = literals.Length - literalPosition;
      EnsureRawBlockCapacity(output.Count - blockOutputStart, finalLiterals);
      AddBytes(output, literals[literalPosition..]);

      if (output.Count - blockOutputStart < LastLiterals)
        throw new InvalidDataException("Compressed Lizard block violates the required final-literals tail.");
    }
  }

  private static void EmitSequence(Stream flags, Stream literals, byte[] source,
      int literalStart, int matchStart, int offset, int matchLength) {
    var literalLength = matchStart - literalStart;
    var matchCode = matchLength - MinMatch;
    var literalNibble = Math.Min(literalLength, 15);
    var matchNibble = Math.Min(matchCode, 15);

    flags.WriteByte((byte)((matchNibble << 4) | literalNibble));
    if (literalNibble == 15)
      WriteExtendedLength(literals, literalLength - 15);
    literals.Write(source.AsSpan(literalStart, literalLength));

    Span<byte> offsetBuffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(offsetBuffer, (ushort)offset);
    literals.Write(offsetBuffer);

    if (matchNibble == 15)
      WriteExtendedLength(literals, matchCode - 15);
  }

  private static (int Length, int Offset) FindBestMatch(byte[] source, int position,
      int[] heads, int[] previous, int searchDepth) {
    if (position + MinMatch > source.Length)
      return (0, 0);

    var candidate = heads[Hash4(source, position)];
    var bestLength = 0;
    var bestOffset = 0;
    var maxLength = source.Length - LastLiterals - position;

    for (var attempts = 0; candidate >= 0 && attempts < searchDepth; ++attempts) {
      var offset = position - candidate;
      if (offset > MaxOffset)
        break;

      if (source[candidate] == source[position] &&
          source[candidate + 1] == source[position + 1] &&
          source[candidate + 2] == source[position + 2] &&
          source[candidate + 3] == source[position + 3]) {
        var length = MinMatch;
        while (length < maxLength && source[candidate + length] == source[position + length])
          ++length;
        if (length > bestLength) {
          bestLength = length;
          bestOffset = offset;
          if (length == maxLength)
            break;
        }
      }

      candidate = previous[candidate];
    }

    return (bestLength, bestOffset);
  }

  private static void Insert(byte[] source, int position, int[] heads, int[] previous) {
    if (position + MinMatch > source.Length)
      return;
    var hash = Hash4(source, position);
    previous[position] = heads[hash];
    heads[hash] = position;
  }

  private static int Hash4(byte[] source, int position) =>
    (int)((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position)) * 2654435761u) >> (32 - HashBits));

  private static int GetSearchDepth(int compressionLevel) => compressionLevel switch {
    <= 11 => 1,
    <= 13 => 2,
    14 => 4,
    15 => 8,
    16 => 16,
    17 => 256,
    18 => 256,
    _ => 512,
  };

  private static void WriteExtendedLength(Stream output, int remaining) {
    if (remaining < 254) {
      output.WriteByte((byte)remaining);
      return;
    }

    if (remaining < 1 << 16) {
      output.WriteByte(254);
      Span<byte> value = stackalloc byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)remaining);
      output.Write(value);
      return;
    }

    if (remaining >= 1 << 24)
      throw new InvalidOperationException("Lizard length exceeds the 24-bit format limit.");
    output.WriteByte(255);
    WriteUInt24(output, remaining);
  }

  private static int ReadExtendedLength(ReadOnlySpan<byte> source, ref int position) {
    if (position >= source.Length)
      throw new InvalidDataException("Truncated Lizard extended length.");

    var first = source[position++];
    if (first < 254)
      return first;
    if (first == 254) {
      if (source.Length - position < 2)
        throw new InvalidDataException("Truncated Lizard 16-bit extended length.");
      var value = BinaryPrimitives.ReadUInt16LittleEndian(source[position..]);
      position += 2;
      return value;
    }

    return ReadUInt24(source, ref position);
  }

  private static void WriteStream(Stream output, ReadOnlySpan<byte> data) {
    WriteUInt24(output, data.Length);
    output.Write(data);
  }

  private static ReadOnlySpan<byte> ReadStream(ReadOnlySpan<byte> source, ref int position, string name) {
    var length = ReadUInt24(source, ref position);
    if (length > source.Length - position)
      throw new InvalidDataException($"Lizard {name} stream exceeds its block.");
    var result = source.Slice(position, length);
    position += length;
    return result;
  }

  private static void WriteUInt24(Stream output, int value) {
    if ((uint)value > 0xFFFFFFu)
      throw new ArgumentOutOfRangeException(nameof(value));
    Span<byte> bytes = stackalloc byte[3];
    bytes[0] = (byte)value;
    bytes[1] = (byte)(value >> 8);
    bytes[2] = (byte)(value >> 16);
    output.Write(bytes);
  }

  private static int ReadUInt24(ReadOnlySpan<byte> source, ref int position) {
    if (source.Length - position < 3)
      throw new InvalidDataException("Truncated Lizard 24-bit value.");
    var value = source[position] | (source[position + 1] << 8) | (source[position + 2] << 16);
    position += 3;
    return value;
  }

  private static int GetBlockSize(int blockSizeId) => blockSizeId switch {
    1 => 128 * 1024,
    2 => 256 * 1024,
    3 => 1024 * 1024,
    4 => 4 * 1024 * 1024,
    5 => 16 * 1024 * 1024,
    6 => 64 * 1024 * 1024,
    7 => 256 * 1024 * 1024,
    _ => throw new InvalidDataException($"Unsupported Lizard block-size id {blockSizeId}."),
  };

  private static byte ReadByte(Stream input, string field) {
    var value = input.ReadByte();
    if (value < 0)
      throw new EndOfStreamException($"Truncated Lizard {field}.");
    return (byte)value;
  }

  private static void ValidateCompressionLevel(int compressionLevel) {
    if (compressionLevel is < 10 or > 19)
      throw new ArgumentOutOfRangeException(nameof(compressionLevel), compressionLevel,
        "The managed Lizard codec currently supports interoperable fast-LZ4 levels 10 through 19.");
  }

  private static void EnsureRawBlockCapacity(int alreadyDecoded, int additional) {
    if (additional < 0 || alreadyDecoded < 0 || additional > RawBlockSize - alreadyDecoded)
      throw new InvalidDataException("Decoded Lizard internal block exceeds 128 KiB.");
  }

  private static void AddBytes(List<byte> output, ReadOnlySpan<byte> data) {
    foreach (var value in data)
      output.Add(value);
  }

  private static uint XxHash32(ReadOnlySpan<byte> data, uint seed = 0) {
    const uint prime1 = 2654435761u;
    const uint prime2 = 2246822519u;
    const uint prime3 = 3266489917u;
    const uint prime4 = 668265263u;
    const uint prime5 = 374761393u;

    uint hash;
    var position = 0;
    if (data.Length >= 16) {
      var v1 = seed + prime1 + prime2;
      var v2 = seed + prime2;
      var v3 = seed;
      var v4 = seed - prime1;
      var limit = data.Length - 16;
      while (position <= limit) {
        v1 = BitOperations.RotateLeft(v1 + BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) * prime2, 13) * prime1;
        position += 4;
        v2 = BitOperations.RotateLeft(v2 + BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) * prime2, 13) * prime1;
        position += 4;
        v3 = BitOperations.RotateLeft(v3 + BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) * prime2, 13) * prime1;
        position += 4;
        v4 = BitOperations.RotateLeft(v4 + BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) * prime2, 13) * prime1;
        position += 4;
      }
      hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
        + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
    } else {
      hash = seed + prime5;
    }

    hash += (uint)data.Length;
    while (position <= data.Length - 4) {
      hash = BitOperations.RotateLeft(hash + BinaryPrimitives.ReadUInt32LittleEndian(data[position..]) * prime3, 17) * prime4;
      position += 4;
    }
    while (position < data.Length) {
      hash = BitOperations.RotateLeft(hash + data[position] * prime5, 11) * prime1;
      ++position;
    }

    hash ^= hash >> 15;
    hash *= prime2;
    hash ^= hash >> 13;
    hash *= prime3;
    hash ^= hash >> 16;
    return hash;
  }
}
