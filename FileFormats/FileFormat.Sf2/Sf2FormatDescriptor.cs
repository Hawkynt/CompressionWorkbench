#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Sf2;

/// <summary>
/// Exposes a SoundFont 2 bank (<c>.sf2</c>) as a pseudo-archive: <c>FULL.sf2</c>
/// (the byte-exact bank) plus one playable 16-bit mono WAV per sample header
/// (<c>samples/NNN_&lt;name&gt;.wav</c>, each at its own sample rate), an INI summary
/// (<c>metadata.ini</c>) and the INFO sub-chunks as <c>metadata/&lt;id&gt;.txt</c> tags.
/// ROM samples and the terminal <c>EOS</c> sentinel header are skipped. Read-only —
/// rebuilding a valid bank requires the full <c>pdta</c> generator chain.
/// </summary>
public sealed class Sf2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Sf2";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "SoundFont 2";
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
public string DefaultExtension => ".sf2";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sf2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];

  // "RIFF" at offset 0 is shared with WAV (too generic); the "sfbk" form type at
  // offset 8 is the discriminating signature for a SoundFont bank.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("sfbk"u8.ToArray(), Offset: 8, Confidence: 0.95),
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
public string Description => "SoundFont 2 bank; full file + one mono WAV per sample + INFO metadata.";

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

  // ── parsing ────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sf2", "Container", blob),
    };

    var parsed = Sf2Reader.Parse(blob);
    if (parsed == null)
      return entries;

    // INFO tags.
    foreach (var (id, value) in parsed.Info)
      entries.Add(new($"metadata/{id}.txt", "Tag", Encoding.ASCII.GetBytes(value)));

    // Sample WAVs (16-bit mono, each at its own rate; ROM + EOS skipped).
    var index = 0;
    var sampleCount = 0;
    foreach (var sh in parsed.SampleHeaders) {
      if (sh.IsRom || sh.IsEndMarker || sh.End <= sh.Start)
        continue;

      var byteStart = (long)sh.Start * 2;
      var byteEnd = (long)sh.End * 2;
      if (byteStart < 0 || byteEnd > parsed.SmplData.Length || byteEnd <= byteStart) {
        ++index;
        continue;
      }

      var pcm = new byte[byteEnd - byteStart];
      Buffer.BlockCopy(parsed.SmplData, (int)byteStart, pcm, 0, pcm.Length);
      var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: (int)sh.SampleRate, bitsPerSample: 16);

      var safe = SanitizeFileName(sh.Name);
      entries.Add(new($"samples/{index:D3}_{safe}.wav", "Sample", wav));
      ++index;
      ++sampleCount;
    }

    // Summary INI (inserted right after FULL.sf2).
    var info = new StringBuilder();
    if (parsed.BankName.Length > 0) info.AppendLine($"bank_name={parsed.BankName}");
    info.AppendLine($"version={parsed.VersionMajor}.{parsed.VersionMinor}");
    info.AppendLine($"preset_count={parsed.PresetCount}");
    info.AppendLine($"sample_count={sampleCount}");
    if (parsed.HasSm24) info.AppendLine("note=24-bit sm24 extension present (ignored; lower 8 bits dropped)");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var s = sb.ToString().Trim('.', '_', ' ');
    return s.Length == 0 ? "sample" : s;
  }
}
