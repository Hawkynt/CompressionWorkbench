using Compression.Core.Dictionary.Lzs;

namespace Compression.Core.FileSystems.Cvf;

/// <summary>
/// Stacker 3 ("SD-3") per-cluster codec, as specified in <c>docs/CVF-CODECS-ON-DISK.md</c>: a bare
/// Stac LZS token stream (RFC 1974, MSB-first bytes) with no header, closed by the LZS end marker,
/// zero-padded to a byte boundary and followed by one check byte — <c>0xFF</c> XOR every decoded
/// byte. The token grammar is the <see cref="LzsBuildingBlock"/> one; this type adds only the framing.
/// </summary>
public static class Sd3Codec {

  /// <summary>Encodes one cluster: LZS token stream, end marker, byte alignment, check byte.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    LzsBuildingBlock.EncodeStream(data, ms, LzsCompressionLevel.Maximum);
    ms.WriteByte(CheckByte(data));
    return ms.ToArray();
  }

  /// <summary>
  /// Decodes one cluster payload into a buffer of <paramref name="outLen"/> bytes. A stream that ends
  /// before the cluster is full leaves the tail zero, as the Stacker driver does for a file's last
  /// cluster.
  /// </summary>
  /// <exception cref="InvalidDataException">The token stream is malformed or the check byte disagrees.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> payload, int outLen) {
    var output = new byte[outLen];
    var produced = LzsBuildingBlock.DecodeStream(payload, output, out var consumed);
    // The check byte is present whenever the payload extends past the token stream.
    if (consumed < payload.Length && payload[consumed] != CheckByte(output.AsSpan(0, produced)))
      throw new InvalidDataException("SD-3: check byte does not match the decoded data.");
    return output;
  }

  private static byte CheckByte(ReadOnlySpan<byte> data) {
    byte sum = 0xFF;
    foreach (var b in data) sum ^= b;
    return sum;
  }
}
