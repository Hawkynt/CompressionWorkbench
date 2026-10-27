using System.Buffers.Binary;
using Compression.Core.Deflate;

namespace Compression.Core.FileSystems.Cvf;

/// <summary>
/// Per-cluster codecs of the DoubleSpace / DriveSpace / DriveSpace 3 / Stacker compressed volumes,
/// implemented from <c>docs/CVF-CODECS-ON-DISK.md</c>. An MSDBL cluster payload starts with a 16-bit
/// magic and a 16-bit version — <c>"DS"</c> (DS-0-x), <c>"JM"</c> (JM-0-x) or <c>"SQ"</c> (SQ-0-0,
/// raw DEFLATE) — followed by the token stream; a Stacker payload carries no method field and is
/// recognised by its first word.
/// </summary>
public static class CvfLzCodec {
  /// <summary>Method header of a DS-0-0 payload, read as a little-endian 32-bit word.</summary>
  public const uint DS_0_0 = 'D' | 'S' << 8;

  /// <summary>Method header of a JM-0-0 payload, read as a little-endian 32-bit word.</summary>
  public const uint JM_0_0 = 'J' | 'M' << 8;

  /// <summary>Method header of an SQ-0-0 payload, read as a little-endian 32-bit word.</summary>
  public const uint SQ_0_0 = 'S' | 'Q' << 8;

  private const ushort Sd4Signature = 0x0081;
  private const int HeaderBytes = 4;

  /// <summary>
  /// Compresses one cluster. Returns the payload, or <see langword="null"/> when it would not be
  /// smaller than the raw cluster (the caller then stores the cluster raw).
  /// </summary>
  public static byte[]? Compress(ReadOnlySpan<byte> data, CvfLzMethod method, int level) {
    var payload = Encode(data, method, level);
    return payload is not null && payload.Length < data.Length ? payload : null;
  }

  /// <summary>
  /// Encodes a cluster with the given method and always returns the payload, or
  /// <see langword="null"/> for <see cref="CvfLzMethod.Stored"/>. The caller decides whether the
  /// result fits the cluster's sector budget. <see cref="CvfLzMethod.Auto"/> keeps the smaller of DS
  /// and JM.
  /// </summary>
  public static byte[]? Encode(ReadOnlySpan<byte> data, CvfLzMethod method, int level) => method switch {
    CvfLzMethod.Ds => EncodeMsdbl(data, MsdblDialect.Ds, level),
    CvfLzMethod.Jm => EncodeMsdbl(data, MsdblDialect.Jm, level),
    CvfLzMethod.Auto => Smaller(EncodeMsdbl(data, MsdblDialect.Ds, level), EncodeMsdbl(data, MsdblDialect.Jm, level)),
    CvfLzMethod.Sq => EncodeSq(data, level),
    CvfLzMethod.Sd3 => Sd3Codec.Encode(data),
    CvfLzMethod.Sd4 => Sd4Codec.Encode(data, level),
    _ => null,
  };

  /// <summary>
  /// Decompresses an MSDBL cluster payload (DS, JM or SQ, told apart by its header) to exactly
  /// <paramref name="outLen"/> bytes.
  /// </summary>
  /// <exception cref="InvalidDataException">Unknown method header or a malformed stream.</exception>
  public static byte[] Decompress(byte[] payload, int inLen, int outLen) {
    var input = payload.AsSpan(0, Math.Min(inLen, payload.Length));
    if (input.Length < HeaderBytes) throw new InvalidDataException("CVF: cluster payload shorter than its method header.");

    var output = new byte[outLen];
    var stream = input[HeaderBytes..];
    switch (BinaryPrimitives.ReadUInt16LittleEndian(input)) {
      case (ushort)DS_0_0: MsdblLzCodec.Decode(stream, output, MsdblDialect.Ds, stopAtEndOfInput: false); break;
      case (ushort)JM_0_0: MsdblLzCodec.Decode(stream, output, MsdblDialect.Jm, stopAtEndOfInput: false); break;
      case (ushort)SQ_0_0: DecodeSq(stream, output); break;
      case var magic: throw new InvalidDataException($"CVF: unknown cluster method header 0x{magic:X4}.");
    }
    return output;
  }

  /// <summary>
  /// Decompresses a Stacker cluster payload to <paramref name="outLen"/> bytes. Stacker records no
  /// method per cluster; the payload's first little-endian word selects it — <c>0x0081</c> SD-4,
  /// <c>0x5344</c> ("DS") the DoubleSpace grammar, anything else SD-3, whose first token is always a
  /// literal and so can never begin with either word.
  /// </summary>
  /// <exception cref="InvalidDataException">A malformed stream.</exception>
  public static byte[] DecompressStacker(byte[] payload, int inLen, int outLen) {
    var input = payload.AsSpan(0, Math.Min(inLen, payload.Length));
    var word = input.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(input) : -1;
    switch (word) {
      case Sd4Signature:
        return Sd4Codec.Decode(payload, input.Length, outLen);
      case (int)DS_0_0 when input.Length >= HeaderBytes:
        var output = new byte[outLen];
        MsdblLzCodec.Decode(input[HeaderBytes..], output, MsdblDialect.Ds, stopAtEndOfInput: true);
        return output;
      default:
        return Sd3Codec.Decode(input, outLen);
    }
  }

  private static byte[] EncodeMsdbl(ReadOnlySpan<byte> data, MsdblDialect dialect, int level) {
    var writer = new LsbBitWriter();
    WriteHeader(writer, dialect == MsdblDialect.Ds ? DS_0_0 : JM_0_0);
    MsdblLzCodec.Encode(data, writer, dialect, level);
    return writer.ToArray(alignment: 2);
  }

  private static byte[] EncodeSq(ReadOnlySpan<byte> data, int level) {
    var deflateLevel = level switch {
      <= 1 => DeflateCompressionLevel.Fast,
      2 => DeflateCompressionLevel.Default,
      3 => DeflateCompressionLevel.Best,
      _ => DeflateCompressionLevel.Maximum,
    };
    var stream = DeflateCompressor.Compress(data, deflateLevel);
    var payload = new byte[HeaderBytes + stream.Length + (stream.Length & 1)];
    BinaryPrimitives.WriteUInt32LittleEndian(payload, SQ_0_0);
    stream.CopyTo(payload, HeaderBytes);
    return payload;
  }

  // The DEFLATE stream ends at its final block or when the cluster is full, whichever comes first.
  private static void DecodeSq(ReadOnlySpan<byte> stream, Span<byte> output) {
    try {
      using var input = new MemoryStream(stream.ToArray(), writable: false);
      var inflater = new DeflateDecompressor(input);
      var filled = 0;
      while (filled < output.Length) {
        var n = inflater.Decompress(output[filled..]);
        if (n == 0) break;
        filled += n;
      }
    } catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException) {
      throw new InvalidDataException("SQ: malformed DEFLATE stream.", e);
    }
  }

  private static void WriteHeader(LsbBitWriter writer, uint header) {
    for (var i = 0; i < HeaderBytes; ++i) writer.WriteByte((byte)(header >> (8 * i)));
  }

  private static byte[] Smaller(byte[] a, byte[] b) => b.Length < a.Length ? b : a;
}
