#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Rf64;

/// <summary>
/// Assembles a valid RF64 / BWF container from interleaved little-endian PCM. The
/// written file always carries a leading <c>ds64</c> chunk holding the real 64-bit
/// <c>riffSize</c>/<c>dataSize</c>, and uses the <c>0xFFFFFFFF</c> sentinel in both
/// the top-level <c>RF64</c> size and the <c>data</c> chunk size so the result is a
/// conformant RF64 image that round-trips through <see cref="Rf64Reader"/>.
/// </summary>
public static class Rf64Writer {

  private const uint SizeSentinel = 0xFFFFFFFF;
  // ds64 body: int64 riffSize | int64 dataSize | int64 sampleCount | uint32 tableLength (0 entries).
  private const int Ds64BodySize = 8 + 8 + 8 + 4;
  private const int FmtBodySize = 16;

  /// <summary>
  /// Builds an RF64 blob. <paramref name="formatCode"/> is the WAVE format tag
  /// (1 = integer PCM, 3 = IEEE float). When <paramref name="bext"/> is non-null it
  /// is written verbatim as a <c>bext</c> chunk (Broadcast Audio Extension).
  /// </summary>
  public static byte[] Build(byte[] pcm, int channels, int sampleRate, int bitsPerSample, int formatCode, byte[]? bext) {
    var byteRate = sampleRate * channels * bitsPerSample / 8;
    var blockAlign = (ushort)(channels * bitsPerSample / 8);
    var dataSize = pcm.Length;
    var dataPad = dataSize & 1; // word-align the data body.

    var bextChunkLen = bext is { Length: > 0 } ? 8 + bext.Length + (bext.Length & 1) : 0;

    // Layout: "RF64" + size + "WAVE" | ds64 (8+body) | fmt (8+16) | bext? | data (8 + dataSize + pad).
    var total =
        12                              // RF64 + size + WAVE
      + (8 + Ds64BodySize)              // ds64
      + (8 + FmtBodySize)               // fmt
      + bextChunkLen                    // bext (optional)
      + (8 + dataSize + dataPad);       // data

    // riffSize is the size of everything after the first 8 bytes (i.e. from "WAVE" onward).
    long riffSize = total - 8;

    var buf = new byte[total];
    var s = buf.AsSpan();
    var p = 0;

    "RF64"u8.CopyTo(s[p..]); p += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(s[p..], SizeSentinel); p += 4;
    "WAVE"u8.CopyTo(s[p..]); p += 4;

    // ds64
    "ds64"u8.CopyTo(s[p..]); p += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(s[p..], Ds64BodySize); p += 4;
    BinaryPrimitives.WriteInt64LittleEndian(s[p..], riffSize); p += 8;     // riffSize
    BinaryPrimitives.WriteInt64LittleEndian(s[p..], dataSize); p += 8;     // dataSize
    var sampleCount = blockAlign > 0 ? (long)dataSize / blockAlign : 0;
    BinaryPrimitives.WriteInt64LittleEndian(s[p..], sampleCount); p += 8;  // sampleCount (frames)
    BinaryPrimitives.WriteUInt32LittleEndian(s[p..], 0u); p += 4;          // tableLength

    // fmt
    "fmt "u8.CopyTo(s[p..]); p += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(s[p..], FmtBodySize); p += 4;
    BinaryPrimitives.WriteUInt16LittleEndian(s[p..], (ushort)formatCode); p += 2;
    BinaryPrimitives.WriteUInt16LittleEndian(s[p..], (ushort)channels); p += 2;
    BinaryPrimitives.WriteUInt32LittleEndian(s[p..], (uint)sampleRate); p += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(s[p..], (uint)byteRate); p += 4;
    BinaryPrimitives.WriteUInt16LittleEndian(s[p..], blockAlign); p += 2;
    BinaryPrimitives.WriteUInt16LittleEndian(s[p..], (ushort)bitsPerSample); p += 2;

    // bext (optional)
    if (bextChunkLen > 0) {
      "bext"u8.CopyTo(s[p..]); p += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(s[p..], (uint)bext!.Length); p += 4;
      bext.CopyTo(s[p..]); p += bext.Length;
      if ((bext.Length & 1) == 1) { s[p] = 0; p += 1; }
    }

    // data — size sentinel; real length lives in ds64.dataSize.
    "data"u8.CopyTo(s[p..]); p += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(s[p..], SizeSentinel); p += 4;
    pcm.CopyTo(s[p..]); p += dataSize;
    if (dataPad == 1) { s[p] = 0; p += 1; }

    return buf;
  }
}
