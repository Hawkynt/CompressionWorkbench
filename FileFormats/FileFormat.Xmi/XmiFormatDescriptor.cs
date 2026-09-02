#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Xmi;

/// <summary>
/// Surfaces a Miles XMIDI (<c>.xmi</c>) file as a read-only pseudo-archive:
/// <c>FULL.xmi</c> (the byte-exact IFF file), <c>metadata.ini</c> (song count and
/// per-song timbre lists), and one converted Standard MIDI File per song under
/// <c>songs/NN.mid</c>. Falls back to FULL-only when no song can be converted.
/// </summary>
public sealed class XmiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Xmi";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Miles XMIDI";
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
public string DefaultExtension => ".xmi";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".xmi"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("FORM"u8.ToArray(), Confidence: 0.5),
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
public string Description => "Miles XMIDI; full file + XMI→MIDI conversion per song + timbre metadata.";

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
      new("FULL.xmi", "Container", blob),
    };

    // Require the FORM…XDIR signature within the first 12 bytes.
    if (blob.Length < 12 ||
        blob[0] != 'F' || blob[1] != 'O' || blob[2] != 'R' || blob[3] != 'M' ||
        blob[8] != 'X' || blob[9] != 'D' || blob[10] != 'I' || blob[11] != 'R')
      return entries;

    try {
      var songs = XmiToMidiConverter.Convert(blob);
      var ini = new StringBuilder();
      ini.AppendLine("; XMI metadata");
      ini.Append("songs=").AppendLine(songs.Count.ToString(CultureInfo.InvariantCulture));

      for (var i = 0; i < songs.Count; ++i) {
        entries.Add(new($"songs/{i:D2}.mid", "Track", songs[i].Midi));
        if (songs[i].Timbres.Count > 0)
          ini.Append("song").Append(i.ToString("D2", CultureInfo.InvariantCulture))
             .Append("_timbres=").AppendLine(string.Join(',', songs[i].Timbres));
      }
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));
    } catch (InvalidDataException) {
      // Graceful FULL-only fallback.
    }

    return entries;
  }
}
