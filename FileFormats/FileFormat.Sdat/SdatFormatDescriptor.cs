#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Swav;

namespace FileFormat.Sdat;

/// <summary>
/// Exposes a Nintendo DS sound archive (<c>.sdat</c>) as a pseudo-archive: <c>FULL.sdat</c> (the
/// byte-exact archive), a <c>metadata.ini</c> summary, and every carried file surfaced by its own
/// magic. <c>SWAV</c> samples are embedded raw (<c>files/NNN.swav</c>, Kind <c>Sample</c>) and also
/// decoded to <c>files/NNN.wav</c>; <c>SWAR</c> wave archives have each contained wave decoded as a
/// sample; <c>SSEQ</c>/<c>SBNK</c>/<c>STRM</c> and other carried files are surfaced raw
/// (<c>files/NNN.&lt;type&gt;</c>, Kind <c>Stream</c>). Names are taken from FAT indices. Read-only.
/// </summary>
public sealed class SdatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Sdat";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "SDAT (Nintendo DS sound archive)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Audio;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".sdat";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sdat"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SDAT"u8.ToArray(), Confidence: 0.95),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "SDAT (Nintendo DS sound archive); full file + carried SWAV/SWAR/SSEQ/SBNK files + metadata.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sdat", "Container", blob),
    };

    SdatReader.ParsedSdat? parsed = null;
    try {
      parsed = new SdatReader().Read(blob);
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      return entries;
    }

    var counts = new Dictionary<string, int>();
    var swavReader = new SwavReader();
    var swarReader = new SwarReader();

    foreach (var f in parsed.Files) {
      var trimmed = f.Magic.Trim();
      switch (trimmed) {
        case "SWAV": {
          entries.Add(new($"files/{f.Index:D3}.swav", "Sample", f.Data));
          TryAddSwavWav(entries, swavReader, f.Index, f.Data);
          Bump(counts, "swav");
          break;
        }
        case "SWAR": {
          entries.Add(new($"files/{f.Index:D3}.swar", "Stream", f.Data));
          TryAddSwarWaves(entries, swarReader, f.Index, f.Data);
          Bump(counts, "swar");
          break;
        }
        default: {
          var ext = trimmed.Length > 0 ? trimmed.ToLowerInvariant() : "bin";
          entries.Add(new($"files/{f.Index:D3}.{ext}", "Stream", f.Data));
          Bump(counts, ext);
          break;
        }
      }
    }

    entries.Insert(1, new("metadata.ini", "Tag", BuildMetadata(parsed, counts)));
    return entries;
  }

  private static void TryAddSwavWav(
      List<AudioPseudoArchive.Entry> entries, SwavReader reader, int index, byte[] data) {
    try {
      var parsed = reader.Read(data);
      var pcm = SwavReader.ShortsToLe(parsed.Pcm);
      var wav = PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate, bitsPerSample: 16);
      entries.Add(new($"files/{index:D3}.wav", "Sample", wav, "pcm"));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      // Undecodable SWAV: keep the raw .swav only.
    }
  }

  private static void TryAddSwarWaves(
      List<AudioPseudoArchive.Entry> entries, SwarReader reader, int index, byte[] data) {
    try {
      var waves = reader.Read(data);
      for (var w = 0; w < waves.Count; ++w) {
        var pcm = SwavReader.ShortsToLe(waves[w].Pcm);
        var wav = PcmCodec.ToWavBlob(pcm, channels: 1, waves[w].SampleRate, bitsPerSample: 16);
        entries.Add(new($"samples/{index:D3}_{w:D3}.wav", "Sample", wav, "pcm"));
      }
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      // Undecodable SWAR: keep the raw .swar only.
    }
  }

  private static void Bump(Dictionary<string, int> counts, string key)
    => counts[key] = counts.GetValueOrDefault(key) + 1;

  private static byte[] BuildMetadata(SdatReader.ParsedSdat parsed, Dictionary<string, int> counts) {
    var sb = new StringBuilder();
    sb.Append("[sdat]\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={parsed.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"fileCount={parsed.Files.Count}\n");
    foreach (var (k, v) in counts.OrderBy(kv => kv.Key))
      sb.Append(CultureInfo.InvariantCulture, $"{k}={v}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
