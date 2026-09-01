using System.Buffers.Binary;
using Compression.Core.Dictionary.QuickLz;

namespace FileFormat.QuickLz;

/// <summary>Reads and writes non-streaming QuickLZ 1.5.0 level-1 packets.</summary>
public static class QuickLzStream {
  private const int ShortHeaderSize = 3;
  private const int LongHeaderSize = 9;
  private const int ShortHeaderLimit = 216;
  private const byte CompressedFlag = 0x01;
  private const byte LongHeaderFlag = 0x02;
  private const byte Level1Flag = 0x04;
  private const byte StreamingMask = 0x30;
  private const byte RequiredFlag = 0x40;
  private const byte ReservedFlag = 0x80;

  /// <summary>Compresses one packet using QuickLZ 1.5.0 level 1 without streaming state.</summary>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var source = new MemoryStream();
    input.CopyTo(source);
    var plain = source.ToArray();
    var payload = QuickLzCompressor.Compress(plain);
    var useCompressed = payload.Length < plain.Length;
    var longHeader = plain.Length >= ShortHeaderLimit;
    var headerSize = longHeader ? LongHeaderSize : ShortHeaderSize;
    var storedPayload = useCompressed ? payload : plain;
    var totalSize = checked(headerSize + storedPayload.Length);

    if (!longHeader && totalSize > byte.MaxValue)
      throw new InvalidDataException("QuickLZ short packet does not fit its one-byte compressed-size field.");

    var flags = (byte)(RequiredFlag | Level1Flag |
      (longHeader ? LongHeaderFlag : 0) |
      (useCompressed ? CompressedFlag : 0));
    WriteHeader(output, flags, totalSize, plain.Length, longHeader);
    output.Write(storedPayload);
  }

  /// <summary>Decompresses one non-streaming QuickLZ 1.5.0 level-1 packet.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var first = input.ReadByte();
    if (first < 0)
      throw new InvalidDataException("QuickLZ stream is empty.");
    var flags = (byte)first;
    ValidateFlags(flags);

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

    var expanded = QuickLzDecompressor.Decompress(payload, expandedLength);
    output.Write(expanded);
  }

  private static void ValidateFlags(byte flags) {
    if ((flags & RequiredFlag) == 0)
      throw new InvalidDataException("QuickLZ packet is missing mandatory flag bit 6.");
    if ((flags & ReservedFlag) != 0)
      throw new InvalidDataException("QuickLZ packet uses reserved flag bit 7.");
    if ((flags & StreamingMask) != 0)
      throw new NotSupportedException("QuickLZ streaming-state packets are not supported by this stateless descriptor.");
    var level = (flags >> 2) & 0x03;
    if (level != 1)
      throw new NotSupportedException($"QuickLZ compression level {level} is not supported; this descriptor implements level 1.");
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
