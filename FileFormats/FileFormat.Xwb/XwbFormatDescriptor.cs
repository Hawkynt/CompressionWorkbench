#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Xwb;

/// <summary>
/// Exposes a Microsoft XACT Wave Bank (<c>.xwb</c>) as a pseudo-archive: <c>FULL.xwb</c> (the
/// byte-exact bank), a <c>metadata.ini</c> summary, and one playable WAV per decodable entry
/// (<c>samples/NNN_&lt;name&gt;.wav</c>, names taken from ENTRYNAMES when present, Kind <c>Sample</c>).
/// PCM (8/16-bit) and MS-ADPCM entries decode; XMA/WMA entries are skipped with a metadata note.
/// Read-only — rebuilding a bank requires the full XACT toolchain.
/// </summary>
public sealed class XwbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Xwb";
  public string DisplayName => "XACT Wave Bank";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".xwb";
  public IReadOnlyList<string> Extensions => [".xwb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("WBND"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "XACT Wave Bank (.xwb); full file + one WAV per PCM/ADPCM entry + metadata.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.xwb", "Container", blob),
    };

    XwbReader.ParsedXwb? parsed = null;
    try {
      parsed = new XwbReader().Read(blob);
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      return entries;
    }

    var decoded = 0;
    var skipped = 0;
    foreach (var e in parsed.Entries) {
      if (!e.Decodable || e.Pcm == null) {
        ++skipped;
        continue;
      }
      var channels = Math.Max(1, e.Channels);
      var pcm = ShortsToLe(e.Pcm);
      var wav = PcmCodec.ToWavBlob(pcm, channels, e.SampleRate, bitsPerSample: 16);
      var safe = SanitizeFileName(e.Name);
      entries.Add(new($"samples/{e.Index:D3}_{safe}.wav", "Sample", wav, "pcm"));
      ++decoded;
    }

    entries.Insert(1, new("metadata.ini", "Tag", BuildMetadata(parsed, decoded, skipped)));
    return entries;
  }

  private static byte[] BuildMetadata(XwbReader.ParsedXwb parsed, int decoded, int skipped) {
    var sb = new StringBuilder();
    sb.Append("[xwb]\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={parsed.Version}\n");
    if (parsed.Bank.BankName.Length > 0)
      sb.Append(CultureInfo.InvariantCulture, $"bankName={parsed.Bank.BankName}\n");
    sb.Append(CultureInfo.InvariantCulture, $"entryCount={parsed.Bank.EntryCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"decoded={decoded}\n");
    sb.Append(CultureInfo.InvariantCulture, $"skipped={skipped}\n");
    foreach (var e in parsed.Entries) {
      var tagName = e.FormatTag switch {
        0 => "PCM",
        1 => "XMA",
        2 => "MS-ADPCM",
        3 => "WMA",
        _ => $"tag{e.FormatTag}",
      };
      var note = e.FormatTag is 1 or 3 ? " (skipped: codec not supported)" : e.Decodable ? "" : " (skipped: unreadable)";
      sb.Append(CultureInfo.InvariantCulture,
        $"entry{e.Index}={e.Name};codec={tagName};channels={e.Channels};rate={e.SampleRate};bits={e.BitsPerSample}{note}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ShortsToLe(short[] samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var s = sb.ToString().Trim('.', '_', ' ');
    return s.Length == 0 ? "wave" : s;
  }
}
