#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ptm;

/// <summary>
/// Exposes a PolyTracker (PTM) module as an archive of <c>FULL.ptm</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c> (raw 64-row packed pattern
/// blocks located via the parapointer table, NO decode) and
/// <c>samples/NN_{name}.raw</c> per non-empty instrument (raw PCM). The PTM layout
/// was recovered through binary inspection of the documented PolyTracker file
/// format and the OpenMPT/libmodplug loaders.
/// </summary>
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
  public string Description => "PolyTracker module; full file + packed pattern blocks + raw PCM samples.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Parse(ms.ToArray());
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> Parse(byte[] blob) {
    var entries = new List<(string, string, byte[])> {
      ("FULL.ptm", "Track", blob),
    };
    var validMagic = blob.Length >= 48 && blob.AsSpan(44, 4).SequenceEqual("PTMF"u8);
    if (!validMagic) {
      AddPartial(entries);
      return entries;
    }

    var title = ReadAsciiTrim(blob, 0, 28);
    var version = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(30, 2));
    var numOrders = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2));
    var numInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2));
    var numPatterns = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(38, 2));
    var numChannels = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(40, 2));
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(42, 2));

    // Order table: 256 bytes at offset 64. Channel pan table: 32 bytes at 320.
    // Instrument headers: 80 bytes each at offset 352.
    const int instrHdrOff = 352;
    var samples = new List<(string Name, long Length)>();
    for (var s = 0; s < numInstruments; ++s) {
      var off = instrHdrOff + s * 80;
      if (off + 80 > blob.Length) break;
      // type(1) at +0, dosName(12) at +1, flags(1) at +13, volume(1)...,
      // length u32 at +16, dataOffset u32 (file offset) at +28? Layout:
      //   +0 type, +1 filename[12], +13 volume, +14 c4speed u16,
      //   +16 sampleSegment u16, +18 fileOffset u32, +22 length u32, ...
      // PTM stores absolute file offset and byte length of the sample.
      var fileOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 18, 4));
      var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 22, 4));
      var name = ReadAsciiTrim(blob, off + 1, 12);
      var sampleName = ReadAsciiTrim(blob, off + 47, 28);
      var label = string.IsNullOrWhiteSpace(sampleName) ? name : sampleName;
      if (length > 0 && fileOffset > 0 && fileOffset < blob.Length) {
        var take = (int)Math.Min(length, blob.Length - fileOffset);
        if (take > 0) {
          var data = new byte[take];
          Buffer.BlockCopy(blob, (int)fileOffset, data, 0, take);
          var safe = string.IsNullOrWhiteSpace(label) ? "sample" : SanitizeFileName(label);
          entries.Add(($"samples/{(s + 1):D2}_{safe}.raw", "Sample", data));
        }
      }
      samples.Add((label, length));
    }

    // Pattern parapointer table follows the instrument headers: numPatterns u16 entries
    // (parapointers, multiply by 16 for the file offset). Each pattern is up to 64 rows.
    var patternParaOff = instrHdrOff + numInstruments * 80;
    var emitted = 0;
    for (var p = 0; p < numPatterns; ++p) {
      var pp = patternParaOff + p * 2;
      if (pp + 2 > blob.Length) break;
      var para = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pp, 2));
      if (para == 0) continue;
      var off = para * 16;
      if (off >= blob.Length) continue;
      // Pattern length is not stored; bound it by the next non-zero parapointer or EOF.
      var endOff = blob.Length;
      for (var q = 0; q < numPatterns; ++q) {
        if (q == p) continue;
        var qp = patternParaOff + q * 2;
        if (qp + 2 > blob.Length) continue;
        var qpara = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(qp, 2));
        var qoff = qpara * 16;
        if (qpara != 0 && qoff > off && qoff < endOff) endOff = qoff;
      }
      var len = Math.Max(0, endOff - off);
      if (len <= 0) continue;
      var data = new byte[len];
      Buffer.BlockCopy(blob, off, data, 0, len);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data));
      ++emitted;
    }

    var info = new StringBuilder();
    info.AppendLine($"title={title}");
    info.AppendLine($"format=PTM");
    info.AppendLine($"version=0x{version:X4}");
    info.AppendLine($"channels={numChannels}");
    info.AppendLine($"num_orders={numOrders}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_patterns_emitted={emitted}");
    info.AppendLine($"num_instruments={numInstruments}");
    info.AppendLine($"flags=0x{flags:X4}");
    info.AppendLine($"sample_format=8/16-bit PCM (per-instrument flags)");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static void AddPartial(List<(string, string, byte[])> entries) {
    var info = new StringBuilder();
    info.AppendLine("parse_status=partial");
    info.AppendLine("format=PTM");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
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
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
