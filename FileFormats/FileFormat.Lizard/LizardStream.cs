#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Lizard;

/// <summary>
/// Managed Lizard (formerly LZ5) frame codec covering all four Lizard v2 method
/// families: fastLZ4, LIZv1, fastLZ4+HUF, and LIZv1+HUF (levels 10-49).
/// </summary>
/// <remarks>
/// This is a clean-room implementation from the published Lizard block/frame
/// descriptions and the public behavior of the BSD-licensed reference codec.
/// Lizard's fastLZ4 codewords live inside Lizard's five-stream block format and
/// are not ordinary LZ4 blocks. HUF streams use the FiniteStateEntropy wire
/// format implemented by <see cref="LizardHuffman"/>.
/// </remarks>
public static class LizardStream {
  private static readonly byte[] Magic = [0x06, 0x22, 0x4D, 0x18];

  private const byte DefaultFlags = 0x68; // version=01, independent blocks, content size present
  private const int RawBlockSize = 128 * 1024;
  private const int MinMatch = 4;
  private const int MinOffset = 8;
  private const int LastLiterals = 16;
  private const int MatchFindLimit = LastLiterals + MinMatch;
  private const int MaxFastOffset = ushort.MaxValue;
  private const int MaxLizOffset = 0xFFFFFF;
  private const int LongOffsetThreshold = 1 << 16;
  private const int LongOffsetMinMatch = 16;
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int DefaultCompressionLevel = 17;
  private const int DefaultFrameBlockSize = 4 * 1024 * 1024;

  private const byte FlagLiterals = 1;
  private const byte FlagTokens = 2;
  private const byte FlagOffset16 = 4;
  private const byte FlagOffset24 = 8;
  private const byte FlagLengths = 16;
  private const byte FlagUncompressed = 128;

  private static readonly IReadOnlyDictionary<int, byte> BlockSizeIds = new Dictionary<int, byte> {
    [128 * 1024] = 1,
    [256 * 1024] = 2,
    [1024 * 1024] = 3,
    [4 * 1024 * 1024] = 4,
    [16 * 1024 * 1024] = 5,
    [64 * 1024 * 1024] = 6,
    [256 * 1024 * 1024] = 7,
  };

  public static void Compress(Stream input, Stream output) =>
    Compress(input, output, DefaultCompressionLevel, DefaultFrameBlockSize);

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

  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> magic = stackalloc byte[4];
    input.ReadExactly(magic);
    if (!magic.SequenceEqual(Magic))
      throw new InvalidDataException("Not a Lizard stream: invalid magic.");

    var flags = ReadByte(input, "frame flags");
    if ((flags >> 6) != 1)
      throw new InvalidDataException($"Unsupported Lizard frame version {flags >> 6}.");
    if ((flags & 0x03) != 0)
      throw new InvalidDataException("Lizard frame has non-zero reserved flag bits.");

    var blockIndependent = (flags & 0x20) != 0;
    var blockChecksum = (flags & 0x10) != 0;
    var hasContentSize = (flags & 0x08) != 0;
    var contentChecksum = (flags & 0x04) != 0;

    var blockDescriptor = ReadByte(input, "block descriptor");
    if ((blockDescriptor & 0x8F) != 0)
      throw new InvalidDataException("Lizard frame has non-zero reserved block descriptor bits.");
    var maxFrameBlockSize = GetBlockSize((blockDescriptor >> 4) & 7);

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
    if ((byte)(XxHash32(descriptor[..descriptorLength]) >> 8) != expectedHeaderChecksum)
      throw new InvalidDataException("Lizard frame header checksum mismatch.");

    var history = new List<byte>();
    using var decodedFrame = new MemoryStream();
    Span<byte> sizeBuffer = stackalloc byte[4];
    Span<byte> checksumBuffer = stackalloc byte[4];

    while (true) {
      input.ReadExactly(sizeBuffer);
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBuffer);
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
      if (stored)
        history.AddRange(block);
      else
        DecompressDataBlock(block, history);

      var decodedBlockSize = history.Count - blockOutputStart;
      if (decodedBlockSize > maxFrameBlockSize)
        throw new InvalidDataException("Decoded Lizard block exceeds the frame's advertised maximum block size.");
      for (var i = blockOutputStart; i < history.Count; ++i)
        decodedFrame.WriteByte(history[i]);
    }

    var decoded = decodedFrame.ToArray();
    if (contentChecksum) {
      input.ReadExactly(checksumBuffer);
      if (XxHash32(decoded) != BinaryPrimitives.ReadUInt32LittleEndian(checksumBuffer))
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
        output.WriteByte(FlagUncompressed);
        WriteUInt24(output, count);
        output.Write(block);
      } else {
        output.Write(compressed);
      }
    }
    return output.ToArray();
  }

  private static byte[] CompressRawBlock(ReadOnlySpan<byte> source, int compressionLevel) =>
    IsLizV1(compressionLevel)
      ? CompressLizV1Block(source, compressionLevel)
      : CompressFastBlock(source, compressionLevel);

  private static byte[] CompressFastBlock(ReadOnlySpan<byte> source, int compressionLevel) {
    if (source.Length < MatchFindLimit)
      return [];

    var data = source.ToArray();
    CreateMatchTables(data.Length, out var heads, out var previous);
    using var tokens = new MemoryStream();
    using var literals = new MemoryStream();

    var anchor = 0;
    var position = 0;
    var matchStartLimit = data.Length - MatchFindLimit;
    var searchDepth = GetSearchDepth(compressionLevel);
    var lazy = GetStrength(compressionLevel) >= 8;

    while (position <= matchStartLimit) {
      var match = FindBestMatch(data, position, heads, previous, searchDepth, MaxFastOffset, 0);
      if (match.Length < MinMatch) {
        Insert(data, position, heads, previous);
        ++position;
        continue;
      }

      if (lazy && position < matchStartLimit) {
        Insert(data, position, heads, previous);
        var next = FindBestMatch(data, position + 1, heads, previous, searchDepth, MaxFastOffset, 0);
        if (next.Length > match.Length + 1) {
          ++position;
          continue;
        }
      }

      EmitFastSequence(tokens, literals, data, anchor, position, match.Offset, match.Length);
      var end = position + match.Length;
      for (var p = lazy ? position + 1 : position; p < end && p <= matchStartLimit; ++p)
        Insert(data, p, heads, previous);
      position = end;
      anchor = position;
    }

    literals.Write(data.AsSpan(anchor));
    if (tokens.Length == 0)
      return [];
    return BuildCompressedBlock(tokens.ToArray(), literals.ToArray(), [], [], compressionLevel);
  }

  private static byte[] CompressLizV1Block(ReadOnlySpan<byte> source, int compressionLevel) {
    if (source.Length < MatchFindLimit)
      return [];

    var data = source.ToArray();
    CreateMatchTables(data.Length, out var heads, out var previous);
    using var tokens = new MemoryStream();
    using var literals = new MemoryStream();
    using var offsets16 = new MemoryStream();
    using var offsets24 = new MemoryStream();

    var anchor = 0;
    var position = 0;
    var lastOffset = 0;
    var matchStartLimit = data.Length - MatchFindLimit;
    var searchDepth = GetSearchDepth(compressionLevel);
    var lazy = GetStrength(compressionLevel) >= 6;

    while (position <= matchStartLimit) {
      var match = FindBestMatch(data, position, heads, previous, searchDepth, MaxLizOffset, lastOffset);
      if (match.Length < MinMatch) {
        Insert(data, position, heads, previous);
        ++position;
        continue;
      }

      if (lazy && position < matchStartLimit) {
        Insert(data, position, heads, previous);
        var next = FindBestMatch(data, position + 1, heads, previous, searchDepth, MaxLizOffset, lastOffset);
        if (next.Length > match.Length + 1) {
          ++position;
          continue;
        }
      }

      EmitLizV1Sequence(tokens, literals, offsets16, offsets24, data, anchor, position,
        match.Offset, match.Length, ref lastOffset);
      var end = position + match.Length;
      for (var p = lazy ? position + 1 : position; p < end && p <= matchStartLimit; ++p)
        Insert(data, p, heads, previous);
      position = end;
      anchor = position;
    }

    literals.Write(data.AsSpan(anchor));
    if (tokens.Length == 0)
      return [];
    return BuildCompressedBlock(tokens.ToArray(), literals.ToArray(), offsets16.ToArray(), offsets24.ToArray(), compressionLevel);
  }

  private static byte[] BuildCompressedBlock(byte[] tokens, byte[] literals, byte[] offsets16, byte[] offsets24, int compressionLevel) {
    var huffman = UsesHuffman(compressionLevel);
    var lengthsRecord = EncodeStream([], false, out _);
    var offset16Record = EncodeStream(offsets16, false, out _);
    var offset24Record = EncodeStream(offsets24, false, out _);
    var tokenRecord = EncodeStream(tokens, huffman, out var tokensHuffman);
    var literalRecord = EncodeStream(literals, huffman, out var literalsHuffman);

    byte header = 0;
    if (tokensHuffman)
      header |= FlagTokens;
    if (literalsHuffman)
      header |= FlagLiterals;

    using var output = new MemoryStream(1 + lengthsRecord.Length + offset16Record.Length + offset24Record.Length + tokenRecord.Length + literalRecord.Length);
    output.WriteByte(header);
    output.Write(lengthsRecord);
    output.Write(offset16Record);
    output.Write(offset24Record);
    output.Write(tokenRecord);
    output.Write(literalRecord);
    return output.ToArray();
  }

  private static byte[] EncodeStream(ReadOnlySpan<byte> data, bool tryHuffman, out bool huffman) {
    if (tryHuffman && data.Length > 1024 && LizardHuffman.TryCompress(data) is { } compressed
        && compressed.Length + compressed.Length / 8 + 512 < data.Length) {
      using var encoded = new MemoryStream(6 + compressed.Length);
      WriteUInt24(encoded, data.Length);
      WriteUInt24(encoded, compressed.Length);
      encoded.Write(compressed);
      huffman = true;
      return encoded.ToArray();
    }

    using var raw = new MemoryStream(3 + data.Length);
    WriteUInt24(raw, data.Length);
    raw.Write(data);
    huffman = false;
    return raw.ToArray();
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
      if (header == FlagUncompressed) {
        var length = ReadUInt24(source, ref position);
        if (length > RawBlockSize || length > source.Length - position)
          throw new InvalidDataException("Invalid uncompressed Lizard block length.");
        AddBytes(output, source.Slice(position, length));
        position += length;
        continue;
      }
      if ((header & FlagUncompressed) != 0 || (header & ~(FlagLiterals | FlagTokens | FlagOffset16 | FlagOffset24 | FlagLengths)) != 0)
        throw new InvalidDataException($"Unknown Lizard internal block header 0x{header:X2}.");

      var lengths = ReadStream(source, ref position, "lengths", (header & FlagLengths) != 0);
      var offsets16 = ReadStream(source, ref position, "16-bit offsets", (header & FlagOffset16) != 0);
      var offsets24 = ReadStream(source, ref position, "24-bit offsets", (header & FlagOffset24) != 0);
      var tokens = ReadStream(source, ref position, "tokens", (header & FlagTokens) != 0);
      var literals = ReadStream(source, ref position, "literals", (header & FlagLiterals) != 0);
      if (lengths.Length != 0)
        throw new InvalidDataException("Lizard v2 reference streams do not use the historical lengths stream.");

      if (IsLizV1(compressionLevel))
        DecompressLizV1Block(tokens, literals, offsets16, offsets24, output, blockOutputStart);
      else
        DecompressFastBlock(tokens, literals, offsets16, offsets24, output, blockOutputStart);
    }
  }

  private static void DecompressFastBlock(byte[] tokens, byte[] literals, byte[] offsets16, byte[] offsets24,
      List<byte> output, int blockOutputStart) {
    if (offsets16.Length != 0 || offsets24.Length != 0)
      throw new InvalidDataException("Lizard fastLZ4 codewords embed their 16-bit offsets in the literals stream.");

    var literalPosition = 0;
    foreach (var token in tokens) {
      var literalLength = token & 0x0F;
      if (literalLength == 15)
        literalLength += ReadExtendedLength(literals, ref literalPosition);
      CopyLiterals(literals, ref literalPosition, literalLength, output, blockOutputStart);

      if (literals.Length - literalPosition < 2)
        throw new InvalidDataException("Lizard fastLZ4 match is missing its offset.");
      var matchOffset = BinaryPrimitives.ReadUInt16LittleEndian(literals.AsSpan(literalPosition));
      literalPosition += 2;
      if (matchOffset < MinOffset || matchOffset > output.Count)
        throw new InvalidDataException($"Lizard match offset {matchOffset} is invalid at output position {output.Count}.");

      var matchLength = token >> 4;
      if (matchLength == 15)
        matchLength += ReadExtendedLength(literals, ref literalPosition);
      CopyMatch(output, blockOutputStart, matchOffset, matchLength + MinMatch);
    }

    var finalLiterals = literals.Length - literalPosition;
    if (finalLiterals < LastLiterals)
      throw new InvalidDataException("Compressed Lizard block violates the required 16-byte final-literals tail.");
    CopyLiterals(literals, ref literalPosition, finalLiterals, output, blockOutputStart);
  }

  private static void DecompressLizV1Block(byte[] tokens, byte[] literals, byte[] offsets16, byte[] offsets24,
      List<byte> output, int blockOutputStart) {
    var literalPosition = 0;
    var offset16Position = 0;
    var offset24Position = 0;
    var lastOffset = 0;

    foreach (var token in tokens) {
      int matchLength;
      if (token >= 32) {
        var literalLength = token & 7;
        if (literalLength == 7)
          literalLength += ReadExtendedLength(literals, ref literalPosition);
        CopyLiterals(literals, ref literalPosition, literalLength, output, blockOutputStart);

        var repeat = (token & 0x80) != 0;
        if (!repeat) {
          if (offsets16.Length - offset16Position < 2)
            throw new InvalidDataException("LIZv1 token is missing its 16-bit offset.");
          lastOffset = BinaryPrimitives.ReadUInt16LittleEndian(offsets16.AsSpan(offset16Position));
          offset16Position += 2;
          if (lastOffset < MinOffset)
            throw new InvalidDataException("LIZv1 16-bit offset is below the reference minimum of 8.");
        }

        matchLength = (token >> 3) & 15;
        if (matchLength == 15)
          matchLength += ReadExtendedLength(literals, ref literalPosition);
        if (!repeat && matchLength is > 0 and < MinMatch)
          throw new InvalidDataException("LIZv1 new-offset token has a forbidden match length below four.");
        if (matchLength == 0)
          continue; // literal-only prefix before a long-offset token
      } else {
        if (offsets24.Length - offset24Position < 3)
          throw new InvalidDataException("LIZv1 long-offset token is missing its 24-bit offset.");
        lastOffset = ReadUInt24(offsets24, ref offset24Position);
        if (lastOffset < MinOffset)
          throw new InvalidDataException("LIZv1 24-bit offset is invalid.");
        if (token < 31) {
          matchLength = token + LongOffsetMinMatch;
        } else {
          matchLength = 47 + ReadExtendedLength(literals, ref literalPosition);
        }
      }

      if (lastOffset <= 0 || lastOffset > output.Count)
        throw new InvalidDataException($"LIZv1 match offset {lastOffset} is invalid at output position {output.Count}.");
      CopyMatch(output, blockOutputStart, lastOffset, matchLength);
    }

    if (offset16Position != offsets16.Length || offset24Position != offsets24.Length)
      throw new InvalidDataException("LIZv1 offset streams contain unused trailing values.");
    var finalLiterals = literals.Length - literalPosition;
    if (finalLiterals < LastLiterals)
      throw new InvalidDataException("Compressed LIZv1 block violates the required 16-byte final-literals tail.");
    CopyLiterals(literals, ref literalPosition, finalLiterals, output, blockOutputStart);
  }

  private static void EmitFastSequence(Stream tokens, Stream literals, byte[] source,
      int literalStart, int matchStart, int offset, int matchLength) {
    var literalLength = matchStart - literalStart;
    var matchCode = matchLength - MinMatch;
    var literalNibble = Math.Min(literalLength, 15);
    var matchNibble = Math.Min(matchCode, 15);
    tokens.WriteByte((byte)((matchNibble << 4) | literalNibble));
    if (literalNibble == 15)
      WriteExtendedLength(literals, literalLength - 15);
    literals.Write(source.AsSpan(literalStart, literalLength));
    Span<byte> offsetBytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(offsetBytes, checked((ushort)offset));
    literals.Write(offsetBytes);
    if (matchNibble == 15)
      WriteExtendedLength(literals, matchCode - 15);
  }

  private static void EmitLizV1Sequence(Stream tokens, Stream literals, Stream offsets16, Stream offsets24,
      byte[] source, int literalStart, int matchStart, int offset, int matchLength, ref int lastOffset) {
    var literalLength = matchStart - literalStart;
    var repeat = lastOffset != 0 && offset == lastOffset;

    if (repeat) {
      var token = EncodeLizLiteralPrefix(literals, source, literalStart, literalLength);
      token |= 0x80;
      token |= checked((byte)(Math.Min(matchLength, 15) << 3));
      if (matchLength >= 15)
        WriteExtendedLength(literals, matchLength - 15);
      tokens.WriteByte(token);
      return;
    }

    if (offset >= LongOffsetThreshold) {
      if (matchLength < LongOffsetMinMatch)
        throw new InvalidOperationException("LIZv1 long offsets require matches of at least 16 bytes.");
      if (literalLength > 0) {
        var literalToken = EncodeLizLiteralPrefix(literals, source, literalStart, literalLength);
        tokens.WriteByte((byte)(literalToken | 0x80)); // repeat offset + zero match = literal-only prefix
      }

      if (matchLength >= 47) {
        tokens.WriteByte(31);
        WriteExtendedLength(literals, matchLength - 47);
      } else {
        tokens.WriteByte(checked((byte)(matchLength - LongOffsetMinMatch)));
      }
      WriteUInt24(offsets24, offset);
      lastOffset = offset;
      return;
    }

    var shortToken = EncodeLizLiteralPrefix(literals, source, literalStart, literalLength);
    Span<byte> offsetBytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(offsetBytes, checked((ushort)offset));
    offsets16.Write(offsetBytes);
    lastOffset = offset;
    shortToken |= checked((byte)(Math.Min(matchLength, 15) << 3));
    if (matchLength >= 15)
      WriteExtendedLength(literals, matchLength - 15);
    tokens.WriteByte(shortToken);
  }

  private static byte EncodeLizLiteralPrefix(Stream literals, byte[] source, int literalStart, int literalLength) {
    var shortLength = Math.Min(literalLength, 7);
    if (literalLength >= 7)
      WriteExtendedLength(literals, literalLength - 7);
    literals.Write(source.AsSpan(literalStart, literalLength));
    return checked((byte)shortLength);
  }

  private static (int Length, int Offset) FindBestMatch(byte[] source, int position, int[] heads, int[] previous,
      int searchDepth, int maxOffset, int lastOffset) {
    if (position + MinMatch > source.Length)
      return (0, 0);

    var candidate = heads[Hash4(source, position)];
    var bestLength = 0;
    var bestOffset = 0;
    var maxLength = source.Length - LastLiterals - position;
    var attempts = 0;
    while (candidate >= 0 && attempts < searchDepth) {
      var offset = position - candidate;
      if (offset > maxOffset)
        break;
      if (offset < MinOffset) {
        candidate = previous[candidate];
        continue;
      }
      ++attempts;

      if (source[candidate] == source[position] && source[candidate + 1] == source[position + 1]
          && source[candidate + 2] == source[position + 2] && source[candidate + 3] == source[position + 3]) {
        var length = MinMatch;
        while (length < maxLength && source[candidate + length] == source[position + length])
          ++length;
        if (offset >= LongOffsetThreshold && offset != lastOffset && length < LongOffsetMinMatch) {
          candidate = previous[candidate];
          continue;
        }
        if (length > bestLength || length == bestLength && offset == lastOffset) {
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

  private static void CreateMatchTables(int length, out int[] heads, out int[] previous) {
    heads = new int[HashSize];
    previous = new int[length];
    Array.Fill(heads, -1);
    Array.Fill(previous, -1);
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

  private static int GetStrength(int compressionLevel) => compressionLevel % 10;

  private static int GetSearchDepth(int compressionLevel) => GetStrength(compressionLevel) switch {
    <= 1 => 1,
    <= 3 => 2,
    4 => 4,
    5 => 8,
    6 => 16,
    7 => 256,
    8 => 256,
    _ => 512,
  };

  private static bool IsLizV1(int compressionLevel) => compressionLevel / 10 is 2 or 4;
  private static bool UsesHuffman(int compressionLevel) => compressionLevel >= 30;

  private static void CopyLiterals(byte[] literals, ref int position, int length, List<byte> output, int blockOutputStart) {
    EnsureRawBlockCapacity(output.Count - blockOutputStart, length);
    if (length < 0 || length > literals.Length - position)
      throw new InvalidDataException("Lizard literal run exceeds its stream.");
    AddBytes(output, literals.AsSpan(position, length));
    position += length;
  }

  private static void CopyMatch(List<byte> output, int blockOutputStart, int offset, int length) {
    EnsureRawBlockCapacity(output.Count - blockOutputStart, length);
    if (offset < MinOffset || offset > output.Count)
      throw new InvalidDataException($"Lizard match offset {offset} is invalid at output position {output.Count}.");
    var matchStart = output.Count - offset;
    for (var i = 0; i < length; ++i)
      output.Add(output[matchStart + i]);
  }

  private static void WriteExtendedLength(Stream output, int remaining) {
    if (remaining < 0)
      throw new ArgumentOutOfRangeException(nameof(remaining));
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

  private static byte[] ReadStream(ReadOnlySpan<byte> source, ref int position, string name, bool huffman) {
    if (!huffman) {
      var length = ReadUInt24(source, ref position);
      if (length > source.Length - position)
        throw new InvalidDataException($"Lizard {name} stream exceeds its block.");
      var result = source.Slice(position, length).ToArray();
      position += length;
      return result;
    }

    var originalLength = ReadUInt24(source, ref position);
    var compressedLength = ReadUInt24(source, ref position);
    if (compressedLength > source.Length - position)
      throw new InvalidDataException($"Compressed Lizard {name} stream exceeds its block.");
    var result = LizardHuffman.Decompress(source.Slice(position, compressedLength), originalLength);
    position += compressedLength;
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
    var value = source[position] | source[position + 1] << 8 | source[position + 2] << 16;
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
    if (compressionLevel is < 10 or > 49)
      throw new ArgumentOutOfRangeException(nameof(compressionLevel), compressionLevel,
        "Lizard compression levels range from 10 through 49.");
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
