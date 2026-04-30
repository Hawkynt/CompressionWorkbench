using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Lrzip;

/// <summary>
/// Writes a Long Range Zip (lrzip) container with the LZMA subtype.
/// Other methods are not supported on the write path; the format itself documents them
/// only for interop reading.
/// </summary>
public sealed class LrzipWriter {
  /// <summary>Major version to write into the header. Defaults to 0.</summary>
  public byte MajorVersion { get; set; } = LrzipConstants.DefaultMajorVersion;

  /// <summary>Minor version to write into the header. Defaults to 6 (lrzip 0.6).</summary>
  public byte MinorVersion { get; set; } = LrzipConstants.DefaultMinorVersion;

  /// <summary>LZMA properties byte. Defaults to 0x5D (lc=3 lp=0 pb=2).</summary>
  public byte LzmaPropertiesByte { get; set; } = LrzipConstants.DefaultLzmaPropertiesByte;

  /// <summary>LZMA dictionary size in bytes. Defaults to 1 MiB.</summary>
  public int LzmaDictionarySize { get; set; } = LrzipConstants.DefaultLzmaDictionarySize;

  /// <summary>
  /// Compresses <paramref name="input"/> with LZMA and writes a complete lrzip container
  /// (header + body) to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">The stream that receives the lrzip container.</param>
  public void Write(ReadOnlySpan<byte> input, Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Decode the properties byte the same way the LZMA decoder does so we can pass
    // the equivalent (lc, lp, pb) tuple to the encoder. This keeps the user's choice
    // of properties byte authoritative even if it diverges from lrzip's canonical 0x5D.
    int propByte = this.LzmaPropertiesByte;
    if (propByte >= 9 * 5 * 5)
      throw new InvalidDataException($"Invalid LZMA properties byte 0x{propByte:X2}.");
    var lc = propByte % 9;
    propByte /= 9;
    var lp = propByte % 5;
    var pb = propByte / 5;

    // Write the 38-byte header up front; ExpandedSize is the original input length.
    Span<byte> header = stackalloc byte[LrzipConstants.HeaderSize];
    header.Clear();
    LrzipConstants.Magic.CopyTo(header[..4]);
    header[4] = this.MajorVersion;
    header[5] = this.MinorVersion;
    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(6, 8), (ulong)input.Length);
    header[14] = LrzipConstants.MethodLzma;
    header[15] = 0; // flags: no encryption
    header[16] = 0; // hashType: MD5 — written as zeros, not validated by reader
    // header[17..22] — 5 reserved bytes, already zeroed
    // header[22..38] — 16-byte hash, written as zeros (we don't compute MD5)
    output.Write(header);

    // 5-byte LZMA preamble: properties byte + dictionary size LE
    Span<byte> preamble = stackalloc byte[LrzipConstants.LzmaPreambleSize];
    preamble[0] = this.LzmaPropertiesByte;
    BinaryPrimitives.WriteInt32LittleEndian(preamble[1..], this.LzmaDictionarySize);
    output.Write(preamble);

    // Encode without an end-of-stream marker — lrzip relies on ExpandedSize for length.
    var encoder = new LzmaEncoder(this.LzmaDictionarySize, lc, lp, pb);
    encoder.Encode(output, input, writeEndMarker: false);
  }
}
