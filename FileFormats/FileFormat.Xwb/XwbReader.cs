#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.MsAdpcm;

namespace FileFormat.Xwb;

/// <summary>
/// Parses a Microsoft XACT Wave Bank (<c>.xwb</c>, magic <c>WBND</c>, little-endian) into its bank
/// metadata and per-entry decoded PCM. The v43+ layout is targeted: a "WBND" + version header is
/// followed by a five-entry segment table (BANKDATA, ENTRYMETADATA, SEEKTABLES, ENTRYNAMES,
/// ENTRYWAVEDATA), each a (u32 offset, u32 length) pair. Two compact-format wave codecs decode —
/// PCM (8/16-bit) and MS-ADPCM (via <see cref="MsAdpcmCodec"/>); XMA and WMA entries are reported
/// but skipped (no PCM is produced for them).
/// </summary>
public sealed class XwbReader {

  public sealed record BankInfo(
    uint Flags,
    int EntryCount,
    string BankName,
    int EntryMetaDataElementSize,
    int EntryNameElementSize,
    uint Alignment);

  public sealed record EntryInfo(
    int Index,
    string Name,
    int FormatTag,        // 0 PCM, 1 XMA, 2 ADPCM, 3 WMA
    int Channels,
    int SampleRate,
    int BlockAlign,
    int BitsPerSample,    // 8 or 16
    int PlayRegionOffset,
    int PlayRegionLength,
    bool Decodable,
    short[]? Pcm);        // decoded PCM16 (interleaved); null when not decodable

  public sealed record ParsedXwb(int Version, BankInfo Bank, IReadOnlyList<EntryInfo> Entries);

  private const int SegmentCount = 5;
  private const int SegBankData = 0;
  private const int SegEntryMetaData = 1;
  private const int SegEntryNames = 3;
  private const int SegEntryWaveData = 4;

  public ParsedXwb Read(ReadOnlySpan<byte> data) {
    if (data.Length < 8)
      throw new InvalidDataException("XWB too short for header.");
    if (data[0] != 'W' || data[1] != 'B' || data[2] != 'N' || data[3] != 'D')
      throw new InvalidDataException("Missing WBND magic.");

    var version = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

    // After the magic + version word there is a second version word (header version) in v43+;
    // parse defensively by locating the segment table after both. Layout:
    //   [0]  "WBND"
    //   [4]  u32 version
    //   [8]  u32 headerVersion
    //   [12] 5 × (u32 offset, u32 length)  → segment table
    var segTableOffset = 12;
    if (segTableOffset + SegmentCount * 8 > data.Length)
      throw new InvalidDataException("XWB segment table out of range.");

    var segOffset = new int[SegmentCount];
    var segLength = new int[SegmentCount];
    for (var i = 0; i < SegmentCount; ++i) {
      segOffset[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(segTableOffset + i * 8)..]);
      segLength[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(segTableOffset + i * 8 + 4)..]);
    }

    var bank = ReadBankData(data, segOffset[SegBankData]);
    var names = ReadEntryNames(data, segOffset[SegEntryNames], segLength[SegEntryNames],
      bank.EntryCount, bank.EntryNameElementSize);

    var entries = new List<EntryInfo>(bank.EntryCount);
    var metaSize = bank.EntryMetaDataElementSize > 0 ? bank.EntryMetaDataElementSize : 24;
    var waveBase = segOffset[SegEntryWaveData];
    var waveLen = segLength[SegEntryWaveData];

    for (var i = 0; i < bank.EntryCount; ++i) {
      var metaOff = segOffset[SegEntryMetaData] + i * metaSize;
      if (metaOff + 24 > data.Length)
        break;

      var formatWord = BinaryPrimitives.ReadUInt32LittleEndian(data[(metaOff + 4)..]);
      var playOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(metaOff + 8)..]);
      var playLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(metaOff + 12)..]);

      // XACT format word: bits 0-1 tag, 2-4 channels, 5-22 sampleRate (18 bits),
      // 23-30 blockAlign (8 bits), 31 bitsPerSample (0 = 8-bit, 1 = 16-bit).
      var tag = (int)(formatWord & 0x3);
      var channels = (int)((formatWord >> 2) & 0x7);
      var sampleRate = (int)((formatWord >> 5) & 0x3FFFF);
      var alignIndex = (int)((formatWord >> 23) & 0xFF);
      var bits = ((formatWord >> 31) & 0x1) != 0 ? 16 : 8;
      if (channels < 1) channels = 1;

      var blockAlign = tag == 2
        ? (alignIndex + 22) * channels           // MS-ADPCM convention
        : channels * (bits / 8);

      var name = i < names.Count ? names[i] : $"wave_{i:D3}";

      var absStart = waveBase + playOffset;
      var absLen = playLength;
      var decodable = false;
      short[]? pcm = null;

      if (absStart >= 0 && playLength > 0 && absStart + absLen <= data.Length
          && playOffset + playLength <= waveLen) {
        var coded = data.Slice(absStart, absLen);
        try {
          switch (tag) {
            case 0: // PCM
              pcm = DecodePcm(coded, bits);
              decodable = true;
              break;
            case 2: // MS-ADPCM
              pcm = DecodeAdpcm(coded, blockAlign, channels);
              decodable = true;
              break;
            // 1 XMA, 3 WMA → not decodable (reported, skipped).
          }
        } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                       or IndexOutOfRangeException or ArgumentOutOfRangeException) {
          decodable = false;
          pcm = null;
        }
      }

      entries.Add(new EntryInfo(i, name, tag, channels, sampleRate, blockAlign, bits,
        absStart, absLen, decodable, pcm));
    }

    return new ParsedXwb(version, bank, entries);
  }

  private static BankInfo ReadBankData(ReadOnlySpan<byte> data, int offset) {
    if (offset + 0x60 > data.Length)
      throw new InvalidDataException("XWB BANKDATA out of range.");
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    var entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
    var bankName = ReadFixedString(data.Slice(offset + 8, 64));
    var entryMetaSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 72)..]);
    var entryNameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 76)..]);
    var alignment = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 80)..]);
    if (entryCount is < 0 or > 1_000_000)
      throw new InvalidDataException($"Implausible XWB entry count {entryCount}.");
    return new BankInfo(flags, entryCount, bankName, entryMetaSize, entryNameSize, alignment);
  }

  private static IReadOnlyList<string> ReadEntryNames(
      ReadOnlySpan<byte> data, int offset, int length, int entryCount, int nameElementSize) {
    var names = new List<string>();
    if (offset <= 0 || length <= 0 || nameElementSize <= 0)
      return names;
    if (offset + length > data.Length)
      return names;
    for (var i = 0; i < entryCount; ++i) {
      var o = offset + i * nameElementSize;
      if (o + nameElementSize > offset + length)
        break;
      names.Add(ReadFixedString(data.Slice(o, nameElementSize)));
    }
    return names;
  }

  private static string ReadFixedString(ReadOnlySpan<byte> bytes) {
    var len = bytes.IndexOf((byte)0);
    if (len < 0) len = bytes.Length;
    return Encoding.ASCII.GetString(bytes[..len]).Trim();
  }

  private static short[] DecodePcm(ReadOnlySpan<byte> coded, int bits) {
    if (bits == 16) {
      var n = coded.Length / 2;
      var pcm = new short[n];
      for (var i = 0; i < n; ++i)
        pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(coded[(i * 2)..]);
      return pcm;
    }
    // 8-bit PCM in WAV is unsigned; scale to signed 16-bit.
    var pcm8 = new short[coded.Length];
    for (var i = 0; i < coded.Length; ++i)
      pcm8[i] = (short)((coded[i] - 128) << 8);
    return pcm8;
  }

  private static short[] DecodeAdpcm(ReadOnlySpan<byte> coded, int blockAlign, int channels) {
    var perChannel = MsAdpcmCodec.Decode(coded, blockAlign, channels);
    // Re-interleave to match the WAV channel order for ToWavBlob.
    var frames = perChannel[0].Length;
    var interleaved = new short[frames * channels];
    for (var f = 0; f < frames; ++f)
      for (var c = 0; c < channels; ++c)
        interleaved[f * channels + c] = perChannel[c][f];
    return interleaved;
  }
}
