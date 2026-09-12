using System.Buffers.Binary;
using Compression.Core.Dictionary.QuickLz;

namespace FileFormat.QuickLz;

/// <summary>Reads and writes non-streaming QuickLZ 1.5.0 level-1 and level-3 packets.</summary>
public static class QuickLzStream {
  private const int ShortHeaderSize = 3;
  private const int LongHeaderSize = 9;
  private const int ShortHeaderLimit = 216;
  private const byte CompressedFlag = 0x01;
  private const byte LongHeaderFlag = 0x02;
  private const byte StreamingMask = 0x30;
  private const byte RequiredFlag = 0x40;
  private const byte ReservedFlag = 0x80;

  private static readonly int[] OptimalLevel3SearchDepths = [1, 2, 4, 8, QuickLzCompressor.Level3MaxSearchDepth];

  /// <summary>Compresses one packet using the historical QuickLZ 1.5.0 level-1 default.</summary>
  public static void Compress(Stream input, Stream output) =>
    Compress(input, output, QuickLzCompressionLevel.Level1);

  /// <summary>Compresses one packet using the selected QuickLZ level without streaming state.</summary>
  public static void Compress(Stream input, Stream output, QuickLzCompressionLevel level,
      int level3SearchDepth = QuickLzCompressor.Level3MaxSearchDepth) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var plain = ReadAll(input);
    output.Write(BuildPacket(plain, level, level3SearchDepth));
  }

  /// <summary>
  /// Tries level 1 and several legal level-3 match-search depths, then writes the smallest packet.
  /// Ties retain the earlier/faster candidate, starting with level 1.
  /// </summary>
  public static void CompressOptimal(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var plain = ReadAll(input);
    var best = BuildPacket(plain, QuickLzCompressionLevel.Level1, QuickLzCompressor.Level3MaxSearchDepth);
    foreach (var searchDepth in OptimalLevel3SearchDepths) {
      var candidate = BuildPacket(plain, QuickLzCompressionLevel.Level3, searchDepth);
      if (candidate.Length < best.Length)
        best = candidate;
    }

    output.Write(best);
  }

  /// <summary>Decompresses one non-streaming QuickLZ 1.5.0 level-1 or level-3 packet.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var first = input.ReadByte();
    if (first < 0)
      throw new InvalidDataException("QuickLZ stream is empty.");
    var flags = (byte)first;
    var level = ValidateFlags(flags);

    var longHeader = (flags & LongHeaderFlag) != 0;
    var headerSize = longHeader ? LongHeaderSize : ShortHeaderSize;
    Span<byte> header = stackalloc byte[LongHeaderSize];
    header[0] = flags;
    input.ReadExactly(header.Slice(1, headerSize - 1));

    uint totalSize;
    uint expandedSize;
    if (longHeader) {
      totalSize = BinaryPrimitives.ReadUInt32LittleEndian(header[1..5]);
      expandedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[5..9]);
    } else {
      totalSize = header[1];
      expandedSize = header[2];
    }

    if (totalSize < headerSize)
      throw new InvalidDataException("QuickLZ compressed size is smaller than its header.");
    if (totalSize - headerSize > int.MaxValue || expandedSize > int.MaxValue)
      throw new NotSupportedException("QuickLZ packet exceeds the managed in-memory size supported by this implementation.");

    var payloadSize = checked((int)totalSize - headerSize);
    var expandedLength = checked((int)expandedSize);
    var payload = new byte[payloadSize];
    input.ReadExactly(payload);

    if ((flags & CompressedFlag) == 0) {
      if (payloadSize != expandedLength)
        throw new InvalidDataException("QuickLZ stored packet size does not match its expanded size.");
      output.Write(payload);
      return;
    }

    var expanded = QuickLzDecompressor.Decompress(payload, expandedLength, level);
    output.Write(expanded);
  }

  private static byte[] BuildPacket(byte[] plain, QuickLzCompressionLevel level, int level3SearchDepth) {
    var payload = QuickLzCompressor.Compress(plain, level, level3SearchDepth);
    var useCompressed = payload.Length < plain.Length;
    var longHeader = plain.Length >= ShortHeaderLimit;
    var headerSize = longHeader ? LongHeaderSize : ShortHeaderSize;
    var storedPayload = useCompressed ? payload : plain;
    var totalSize = checked(headerSize + storedPayload.Length);

    if (!longHeader && totalSize > byte.MaxValue)
      throw new InvalidDataException("QuickLZ short packet does not fit its one-byte compressed-size field.");

    using var packet = new MemoryStream(totalSize);
    var flags = (byte)(RequiredFlag | ((byte)level << 2) |
      (longHeader ? LongHeaderFlag : 0) |
      (useCompressed ? CompressedFlag : 0));
    WriteHeader(packet, flags, totalSize, plain.Length, longHeader);
    packet.Write(storedPayload);
    return packet.ToArray();
  }

  private static byte[] ReadAll(Stream input) {
    using var source = new MemoryStream();
    input.CopyTo(source);
    return source.ToArray();
  }

  private static QuickLzCompressionLevel ValidateFlags(byte flags) {
    if ((flags & RequiredFlag) == 0)
      throw new InvalidDataException("QuickLZ packet is missing mandatory flag bit 6.");
    if ((flags & ReservedFlag) != 0)
      throw new InvalidDataException("QuickLZ packet uses reserved flag bit 7.");
    if ((flags & StreamingMask) != 0)
      throw new NotSupportedException("QuickLZ streaming-state packets are not supported by this stateless descriptor.");

    return ((flags >> 2) & 0x03) switch {
      1 => QuickLzCompressionLevel.Level1,
      3 => QuickLzCompressionLevel.Level3,
      var level => throw new NotSupportedException($"QuickLZ compression level {level} is not supported; this descriptor implements levels 1 and 3."),
    };
  }

  private static void WriteHeader(Stream output, byte flags, int totalSize, int expandedSize, bool longHeader) {
    Span<byte> header = stackalloc byte[LongHeaderSize];
    header[0] = flags;
    if (longHeader) {
      BinaryPrimitives.WriteUInt32LittleEndian(header[1..5], checked((uint)totalSize));
      BinaryPrimitives.WriteUInt32LittleEndian(header[5..9], checked((uint)expandedSize));
      output.Write(header);
      return;
    }

    header[1] = checked((byte)totalSize);
    header[2] = checked((byte)expandedSize);
    output.Write(header[..ShortHeaderSize]);
  }
}
