#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Swav;

/// <summary>
/// Parses a Nintendo DS <c>.swav</c> sample (and the equivalent SWAVINFO + data record carried
/// inside an <c>SWAR</c> wave archive) into decoded 16-bit mono PCM. Three wave types are
/// recognised: <c>0</c> = signed PCM8, <c>1</c> = PCM16 (little-endian), <c>2</c> = IMA-ADPCM.
/// <para>
/// The NDS IMA-ADPCM variant is the standard IMA scheme: the data record opens with a 4-byte
/// state header (u16 initial predictor, u16 initial step index) followed by nibble pairs, LOW
/// nibble first. The standard 89-entry step table and index-adjust table apply, seeded with the
/// header's initial predictor/index.
/// </para>
/// </summary>
public sealed class SwavReader {

    /// <summary>
  /// Represents a parsed swav.
  /// </summary>
public sealed record ParsedSwav(
    int WaveType,
    bool Loop,
    int SampleRate,
    int Time,
    int LoopOffset,
    int NonLoopLength,
    short[] Pcm);

  /// <summary>Decodes a complete <c>.swav</c> file (NDS header + <c>DATA</c> block).</summary>
  public ParsedSwav Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x18)
      throw new InvalidDataException("SWAV too short for NDS header.");
    if (data[0] != 'S' || data[1] != 'W' || data[2] != 'A' || data[3] != 'V')
      throw new InvalidDataException("Missing SWAV magic.");

    // NDS binary header: magic(4) bom(2) version(2) fileSize(4) headerSize(2) numBlocks(2),
    // then the "DATA" block: marker(4) blockSize(4) then SWAVINFO + sample data.
    if (data[0x10] != 'D' || data[0x11] != 'A' || data[0x12] != 'T' || data[0x13] != 'A')
      throw new InvalidDataException("SWAV missing DATA block.");
    var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
    var infoOffset = 0x18;
    var recordEnd = 0x10 + Math.Max(dataSize, 8);
    if (recordEnd > data.Length)
      recordEnd = data.Length;

    return ReadRecord(data, infoOffset, recordEnd);
  }

  /// <summary>
  /// Decodes a single SWAVINFO record (12-byte info header + sample data) located at
  /// <paramref name="infoOffset"/> and ending at <paramref name="recordEnd"/> (exclusive). Used
  /// for stand-alone <c>.swav</c> files and for each record contained in an <c>SWAR</c> archive.
  /// </summary>
  public ParsedSwav ReadRecord(ReadOnlySpan<byte> data, int infoOffset, int recordEnd) {
    if (infoOffset + 12 > data.Length || recordEnd > data.Length || recordEnd < infoOffset + 12)
      throw new InvalidDataException("SWAVINFO record out of range.");

    // SWAVINFO: waveType(1) loop(1) sampleRate(2) time(2) loopOffset(2) nonLoopLength(4).
    var waveType = data[infoOffset];
    var loop = data[infoOffset + 1] != 0;
    var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(data[(infoOffset + 2)..]);
    var time = BinaryPrimitives.ReadUInt16LittleEndian(data[(infoOffset + 4)..]);
    var loopOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[(infoOffset + 6)..]);
    var nonLoopLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(infoOffset + 8)..]);

    var sampleStart = infoOffset + 12;
    var sampleBytes = data[sampleStart..recordEnd];

    var pcm = waveType switch {
      0 => DecodePcm8(sampleBytes),
      1 => DecodePcm16(sampleBytes),
      2 => DecodeImaAdpcm(sampleBytes),
      _ => throw new InvalidDataException($"Unsupported SWAV wave type {waveType}."),
    };

    return new ParsedSwav(waveType, loop, sampleRate, time, loopOffset, nonLoopLength, pcm);
  }

  /// <summary>Serialises PCM16 samples to little-endian bytes (the WAV sample order).</summary>
  public static byte[] ShortsToLe(short[] samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static short[] DecodePcm8(ReadOnlySpan<byte> bytes) {
    var pcm = new short[bytes.Length];
    for (var i = 0; i < bytes.Length; ++i)
      pcm[i] = (short)((sbyte)bytes[i] << 8);
    return pcm;
  }

  private static short[] DecodePcm16(ReadOnlySpan<byte> bytes) {
    var n = bytes.Length / 2;
    var pcm = new short[n];
    for (var i = 0; i < n; ++i)
      pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]);
    return pcm;
  }

  // Standard IMA tables (matching Codec.ImaAdpcm) so the NDS variant decodes identically given
  // its per-stream initial predictor/index header.
  private static readonly int[] StepTable = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
    724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
    3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
  ];

  private static readonly int[] IndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8];

  private static short[] DecodeImaAdpcm(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < 4)
      return [];

    // 4-byte state header: u16 initial predictor (signed sample), u16 initial step index.
    var predictor = (int)BinaryPrimitives.ReadInt16LittleEndian(bytes);
    var index = (int)BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
    if (index > 88) index = 88;

    var nibbleBytes = bytes[4..];
    // The initial predictor is itself the first decoded sample (per the NDS convention).
    var pcm = new short[1 + nibbleBytes.Length * 2];
    var produced = 0;
    pcm[produced++] = (short)predictor;

    foreach (var b in nibbleBytes) {
      pcm[produced++] = DecodeNibble((byte)(b & 0x0F), ref predictor, ref index); // LOW nibble first
      pcm[produced++] = DecodeNibble((byte)(b >> 4), ref predictor, ref index);
    }

    return pcm;
  }

  private static short DecodeNibble(byte nibble, ref int predictor, ref int index) {
    var step = StepTable[index];
    var diff = step >> 3;
    if ((nibble & 1) != 0) diff += step >> 2;
    if ((nibble & 2) != 0) diff += step >> 1;
    if ((nibble & 4) != 0) diff += step;
    if ((nibble & 8) != 0) predictor -= diff;
    else predictor += diff;
    if (predictor > 32767) predictor = 32767;
    else if (predictor < -32768) predictor = -32768;
    index += IndexAdjust[nibble & 0x07];
    if (index < 0) index = 0;
    else if (index > 88) index = 88;
    return (short)predictor;
  }
}
