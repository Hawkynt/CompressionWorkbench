#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Okt;

/// <summary>
/// Exposes an Oktalyzer (OKT) module as an archive of <c>FULL.okt</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c> (raw PBOD chunk bodies,
/// NO decode) and <c>samples/NN_{name}.raw</c> per SBOD chunk (raw signed 8-bit
/// PCM). The OKT IFF-like chunk layout was recovered through binary inspection of
/// the documented Oktalyzer file format and the OpenMPT/libmodplug loaders.
/// </summary>
public sealed class OktFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Okt";
  public string DisplayName => "OKT (Oktalyzer)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".okt";
  public IReadOnlyList<string> Extensions => [".okt"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("OKTASONG"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Oktalyzer module; full file + PBOD pattern blocks + SBOD raw signed 8-bit PCM samples.";

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
      ("FULL.okt", "Track", blob),
    };
    var validMagic = blob.Length >= 8 && blob.AsSpan(0, 8).SequenceEqual("OKTASONG"u8);
    if (!validMagic) {
      AddPartial(entries);
      return entries;
    }

    var off = 8;
    var channels = 0;
    var patternCount = 0;
    var sampleCount = 0;
    var patternIdx = 0;
    var sampleIdx = 0;
    var sampleNames = new List<string>();

    while (off + 8 <= blob.Length) {
      var id = Encoding.ASCII.GetString(blob, off, 4);
      var len = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(off + 4, 4));
      off += 8;
      if (len < 0 || off + len > blob.Length) break;
      var body = blob.AsSpan(off, len);

      switch (id) {
        case "CMOD":
          // 8 u16 BE channel-mode flags: 1 = mono channel, 2 = a stereo pair.
          for (var i = 0; i + 2 <= len; i += 2) {
            var mode = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(i, 2));
            channels += mode == 1 ? 1 : (mode == 2 ? 2 : 0);
          }
          break;
        case "SAMP":
          // 32 bytes per sample header: name(20) + len u32 BE + ...
          for (var so = 0; so + 32 <= len; so += 32) {
            var nm = ReadAsciiTrim(blob, off + so, 20);
            sampleNames.Add(nm);
          }
          break;
        case "SLEN":
          if (len >= 2) patternCount = BinaryPrimitives.ReadUInt16BigEndian(body[..2]);
          break;
        case "PBOD": {
          var data = body.ToArray();
          entries.Add(($"patterns/pattern_{patternIdx:D2}.bin", "Pattern", data));
          ++patternIdx;
          break;
        }
        case "SBOD": {
          var data = body.ToArray();
          var nm = sampleIdx < sampleNames.Count ? sampleNames[sampleIdx] : "";
          var safe = string.IsNullOrWhiteSpace(nm) ? "sample" : SanitizeFileName(nm);
          entries.Add(($"samples/{(sampleIdx + 1):D2}_{safe}.raw", "Sample", data));
          ++sampleIdx;
          ++sampleCount;
          break;
        }
      }

      off += len;
    }

    var info = new StringBuilder();
    info.AppendLine($"format=OKT");
    info.AppendLine($"channels={(channels > 0 ? channels : 4)}");
    info.AppendLine($"num_patterns={(patternCount > 0 ? patternCount : patternIdx)}");
    info.AppendLine($"num_patterns_emitted={patternIdx}");
    info.AppendLine($"num_samples={sampleCount}");
    info.AppendLine($"sample_format=8-bit signed PCM");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static void AddPartial(List<(string, string, byte[])> entries) {
    var info = new StringBuilder();
    info.AppendLine("parse_status=partial");
    info.AppendLine("format=OKT");
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
