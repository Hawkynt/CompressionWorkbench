#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mus;

/// <summary>
/// Surfaces a DMX/Doom MUS score as a read-only pseudo-archive: <c>FULL.mus</c>
/// (the byte-exact score), <c>metadata.ini</c> (channel and instrument counts), and
/// <c>converted.mid</c> — a Standard MIDI File (format 0) produced by the classic
/// MUS→MIDI mapping. Falls back to FULL-only when the score cannot be converted.
/// </summary>
public sealed class MusFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Mus";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DMX/Doom MUS";
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
public string DefaultExtension => ".mus";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".mus"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'M', (byte)'U', (byte)'S', 0x1A], Confidence: 0.95),
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
public string Description => "DMX/Doom MUS score; full file + MUS→MIDI conversion + metadata.";

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
      new("FULL.mus", "Container", blob),
    };

    if (blob.Length < 16 || blob[0] != 'M' || blob[1] != 'U' || blob[2] != 'S' || blob[3] != 0x1A)
      return entries;

    var primaryChannels = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(8));
    var secondaryChannels = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(10));
    var numInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(12));

    var instruments = new List<int>();
    var instrBase = 16;
    for (var i = 0; i < numInstruments && instrBase + i * 2 + 2 <= blob.Length; ++i)
      instruments.Add(BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(instrBase + i * 2)));

    var ini = new StringBuilder();
    ini.AppendLine("; MUS metadata");
    ini.Append("primary_channels=").AppendLine(primaryChannels.ToString(CultureInfo.InvariantCulture));
    ini.Append("secondary_channels=").AppendLine(secondaryChannels.ToString(CultureInfo.InvariantCulture));
    ini.Append("instruments=").AppendLine(numInstruments.ToString(CultureInfo.InvariantCulture));
    if (instruments.Count > 0)
      ini.Append("instrument_patches=").AppendLine(string.Join(',', instruments));
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    // Best-effort MUS→MIDI conversion; on failure, leave FULL + metadata only.
    try {
      var result = MusToMidiConverter.Convert(blob);
      entries.Add(new("converted.mid", "Track", result.Midi));
    } catch (InvalidDataException) {
      // Graceful fallback.
    }

    return entries;
  }
}
