#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Ptm;

/// <summary>
/// Exposes a PolyTracker (<c>.ptm</c>) module as a read-only pseudo-archive of
/// <c>FULL.ptm</c> (byte-exact original), <c>metadata.ini</c> and one playable mono
/// WAV per instrument that carries sample data under <c>samples/NN_{name}.wav</c>.
/// </summary>
/// <remarks>
/// Layout interpretation (all little-endian). Main header (608 bytes): <c>char[28]
/// name</c>, <c>u8 0x1A</c>, <c>u8 type</c>, <c>u8 reserved</c>, <c>u8 reserved</c>,
/// <c>u16 nOrders</c> (@32), <c>u16 nInstruments</c> (@34), <c>u16 nPatterns</c>
/// (@36), <c>u16 nChannels</c> (@38); the magic <c>"PTMF"</c> sits at offset 44.
/// The 80-byte instrument headers begin at offset 608: <c>u8 type</c>
/// (bit0 = sample present, bit2 = 16-bit), <c>char[12] dosName</c>, <c>u8 volume</c>,
/// <c>u16 c4spd</c>, <c>u16 segment</c>, <c>u32 dataOffset</c>, <c>u32 length</c>
/// (bytes), <c>u32 loopBegin</c>, <c>u32 loopEnd</c>, <c>u32 gusLoopBegin</c>,
/// <c>u32 gusLoopEnd</c>, <c>u8 gusLoopFlags</c>, <c>u8 reserved</c>, <c>char[28]
/// name</c>, <c>char[4] "PTMS"</c>. Sample data lives at <c>dataOffset</c>.
/// PTM 8-bit samples are DELTA-encoded signed (running sum, as in FastTracker 2):
/// each stored byte is added to a running accumulator; the resulting signed value
/// is converted to unsigned-8 for the WAV. PTM 16-bit samples are 16-bit signed
/// little-endian delta (running sum of 16-bit words) and surfaced as 16-bit signed
/// WAV. The per-instrument <c>c4spd</c> sets the WAV sample rate (8363 Hz fallback
/// when zero).
/// </remarks>
public sealed class PtmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Ptm";
  public string DisplayName => "PTM (PolyTracker)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ptm";
  public IReadOnlyList<string> Extensions => [".ptm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PTMF"u8.ToArray(), Offset: 44, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "PolyTracker module; full file + delta-decoded per-sample WAVs.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Parse(ms.ToArray());
  }

  private const int DefaultRate = 8363;
  private const int HeaderSize = 608;
  private const int InstrumentSize = 80;

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.ptm", "Container", blob),
    };
    if (blob.Length < HeaderSize || blob[44] != 'P' || blob[45] != 'T' || blob[46] != 'M' || blob[47] != 'F')
      return entries;

    var name = ReadAsciiTrim(blob, 0, 28);
    var nOrders = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32, 2));
    var nInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2));
    var nPatterns = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2));
    var nChannels = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(38, 2));

    var samplesWithData = 0;
    for (var i = 0; i < nInstruments; ++i) {
      var o = HeaderSize + i * InstrumentSize;
      if (o + InstrumentSize > blob.Length) break;
      var type = blob[o];
      var isSample = (type & 0x01) != 0;
      var is16Bit = (type & 0x04) != 0;
      var dosName = ReadAsciiTrim(blob, o + 1, 12);
      var c4spd = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(o + 15, 2));
      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(o + 19, 4));
      var length = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(o + 23, 4));
      var sName = ReadAsciiTrim(blob, o + 48, 28);
      if (!isSample || length == 0 || dataOffset == 0) continue;
      if (dataOffset >= (uint)blob.Length) continue;
      var avail = blob.Length - (int)dataOffset;
      var take = (int)Math.Min(length, (uint)avail);
      if (take <= 0) continue;
      var rate = c4spd > 0 ? c4spd : DefaultRate;
      byte[] wav;
      if (is16Bit) {
        // 16-bit signed LE delta → running sum.
        var words = take / 2;
        var pcm = new byte[words * 2];
        short acc = 0;
        for (var w = 0; w < words; ++w) {
          var d = BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan((int)dataOffset + w * 2, 2));
          acc = unchecked((short)(acc + d));
          BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(w * 2, 2), acc);
        }
        wav = PcmCodec.ToWavBlob(pcm, channels: 1, rate, bitsPerSample: 16);
      } else {
        // 8-bit signed delta → running sum, then to unsigned-8.
        var pcm = new byte[take];
        sbyte acc = 0;
        for (var j = 0; j < take; ++j) {
          acc = unchecked((sbyte)(acc + unchecked((sbyte)blob[(int)dataOffset + j])));
          pcm[j] = (byte)(acc + 128);
        }
        wav = PcmCodec.ToWavBlob(pcm, channels: 1, rate, bitsPerSample: 8);
      }
      var label = !string.IsNullOrWhiteSpace(sName) ? sName : dosName;
      label = string.IsNullOrWhiteSpace(label) ? "sample" : SanitizeFileName(label);
      entries.Add(new($"samples/{(i + 1):D2}_{label}.wav", "Sample", wav));
      ++samplesWithData;
    }

    var info = new StringBuilder();
    info.AppendLine($"format=PTM");
    info.AppendLine($"name={name}");
    info.AppendLine($"num_orders={nOrders}");
    info.AppendLine($"num_instruments={nInstruments}");
    info.AppendLine($"num_patterns={nPatterns}");
    info.AppendLine($"num_channels={nChannels}");
    info.AppendLine($"samples_with_data={samplesWithData}");
    info.AppendLine($"sample_encoding=delta_signed");
    info.AppendLine($"note=8-bit and 16-bit samples are delta-decoded; c4spd sets WAV rate.");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = Math.Min(offset + length, blob.Length);
    var sb = new StringBuilder();
    for (var i = offset; i < end; ++i) {
      var b = blob[i];
      if (b == 0) break;
      if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
