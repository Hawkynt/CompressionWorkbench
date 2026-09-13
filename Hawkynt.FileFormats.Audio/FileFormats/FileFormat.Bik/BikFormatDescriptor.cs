#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bik;

/// <summary>
/// Surfaces a Bink video container (<c>.bik</c>, Bink 1 'BIK?' and Bink 2 'KB2?') as a
/// packet-aware pseudo-archive. <c>FULL.bik</c> is the byte-exact original;
/// <c>VIDEO.bin</c> and <c>TRACKn.bin</c> are encoded elementary streams, while
/// <c>metadata.ini</c> preserves the container/header fields and per-frame packet boundaries
/// required for packet-preserving mux/remux. Bink 1 audio is additionally decoded to
/// per-channel mono WAV views where supported; Bink 2 audio remains blob-only.
/// </summary>
public sealed class BikFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveCreatable, IArchiveWriteConstraints {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Bik";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Bink Video";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".bik";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".bik", ".bk2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("BIKb"u8.ToArray(), Confidence: 0.95),
    new("BIKf"u8.ToArray(), Confidence: 0.95),
    new("BIKg"u8.ToArray(), Confidence: 0.95),
    new("BIKh"u8.ToArray(), Confidence: 0.95),
    new("BIKi"u8.ToArray(), Confidence: 0.95),
    new("BIKk"u8.ToArray(), Confidence: 0.95),
    new("KB2a"u8.ToArray(), Confidence: 0.9),
    new("KB2d"u8.ToArray(), Confidence: 0.9),
    new("KB2f"u8.ToArray(), Confidence: 0.9),
    new("KB2g"u8.ToArray(), Confidence: 0.9),
    new("KB2h"u8.ToArray(), Confidence: 0.9),
    new("KB2i"u8.ToArray(), Confidence: 0.9),
    new("KB2j"u8.ToArray(), Confidence: 0.9),
    new("KB2k"u8.ToArray(), Confidence: 0.9),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("Stored", "Stored / packet-preserving remux")];
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
  public string Description => "Bink video container (.bik/.bk2); packet-aware demux plus encoded-stream mux/remux without codec re-encoding.";

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

  /// <summary>
  /// Creates a Bink container either as a byte-exact <c>FULL.bik</c> passthrough or by
  /// packet-preserving mux of <c>metadata.ini</c>, <c>VIDEO.bin</c>, and the referenced
  /// <c>TRACKn.bin</c> streams.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    foreach (var input in inputs)
      if (!this.CanAccept(input, out var reason))
        throw new ArgumentException(reason, nameof(inputs));

    BikWriter.Create(output, inputs);
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;

  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "Bink archive accepts: FULL.bik, metadata.ini, VIDEO.bin, TRACKn.bin";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);

    var name = input.ArchiveName.Replace('\\', '/');
    var accepted = !input.IsDirectory && !name.Contains('/') && IsAcceptedLeafName(name);
    if (accepted) {
      reason = null;
      return true;
    }

    reason = $"not a Bink-archive input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  private static bool IsAcceptedLeafName(string name) {
    if (name.Equals("FULL.bik", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("VIDEO.bin", StringComparison.OrdinalIgnoreCase))
      return true;

    const string prefix = "TRACK";
    const string suffix = ".bin";
    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
      return false;

    var trackNumberLength = name.Length - prefix.Length - suffix.Length;
    if (trackNumberLength <= 0)
      return false;

    for (var index = prefix.Length; index < prefix.Length + trackNumberLength; ++index)
      if (name[index] is < '0' or > '9')
        return false;

    return true;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.bik", "Container", blob),
    };
    BikReader.BuildEntries(blob, entries);
    return entries;
  }
}
