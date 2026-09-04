#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.AppleSingle;

/// <summary>
/// Pseudo-archive descriptor for AppleDouble (RFC 1740) sidecar files — the
/// resource fork + Finder metadata Macs leave alongside files when copied to
/// non-HFS filesystems (commonly named <c>._foo</c>). Same on-disk layout as
/// AppleSingle but the data fork lives in the sibling file rather than this one.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc1740</c> — RFC 1740 — carries the AppleSingle/AppleDouble format description as an appendix</description></item>
///   <item><description>Apple "AppleSingle/AppleDouble Formats for Foreign Files Developer's Note" (1990) — the defining vendor document</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/AppleSingle_and_AppleDouble_formats</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class AppleDoubleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "AppleDouble";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "AppleDouble";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".appledouble";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".appledouble"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x05, 0x16, 0x07], Confidence: 0.90),
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
  public string Description =>
    "AppleDouble (RFC 1740) sidecar — Finder metadata + resource fork " +
    "for files copied from HFS to non-HFS filesystems.";

  // Both descriptors delegate to the shared reader.
  private readonly AppleSingleFormatDescriptor _shared = new();

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) => this._shared.List(stream, password);
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    this._shared.Extract(stream, outputDir, password, files);

  /// <summary>
  /// Opens one entry as a bounded stream. Delegated so AppleDouble gets the shared descriptor's
  /// native implementation rather than the interface default, which extracts the whole container
  /// into a temporary directory to read a single entry back.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password)
    => this._shared.OpenEntry(archive, entryName, password);

  /// <inheritdoc cref="AppleSingleFormatDescriptor.ExtractEntryToMemory" />
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password)
    => this._shared.ExtractEntryToMemory(archive, entryName, password);

  // ── IArchiveCreatable ─────────────────────────────────────────────

  /// <summary>
  /// Emits a fresh AppleDouble container. Identical to the AppleSingle body — the same entry-id
  /// namespace, header, directory and payload area — under the AppleDouble magic, because RFC 1740
  /// defines the two as one layout with two headers.
  /// </summary>
  /// <remarks>
  /// The data fork is the one entry an AppleDouble container may not carry: it is what lives in the
  /// sibling file, and a reader that finds it here has been handed a mislabelled AppleSingle. The
  /// synthetic <c>metadata.ini</c> the descriptor surfaces on read is dropped, as it is on the
  /// AppleSingle side; a data fork is refused with a message instead, since silently discarding it
  /// would lose the caller's bytes.
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var entries = new List<(uint EntryId, byte[] Data)>(inputs.Count);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = Path.GetFileName(input.ArchiveName);
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      var id = AppleSingleWriter.EntryIdForName(name);
      if (id == DataForkEntryId)
        throw new NotSupportedException(
          "AppleDouble: the data fork belongs in the sibling file, not in the sidecar. "
          + "Create an AppleSingle container to hold both forks in one file.");
      entries.Add((id, input.ReadContent()));
    }
    var bytes = AppleSingleWriter.Build(entries, AppleSingleReader.MagicDouble);
    output.Position = 0;
    output.Write(bytes, 0, bytes.Length);
    output.SetLength(bytes.Length);
  }

  // ── IArchiveModifiable ────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by id) entries in an existing container, through the same in-place modifier
  /// AppleSingle uses. It rewrites only directory slots and payload ranges and never touches the
  /// leading magic, so an AppleDouble container stays an AppleDouble container.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = Path.GetFileName(input.ArchiveName);
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      var id = AppleSingleWriter.EntryIdForName(name);
      if (id == DataForkEntryId)
        throw new NotSupportedException(
          "AppleDouble: the data fork belongs in the sibling file, not in the sidecar.");
      AppleSingleInPlaceModifier.ReplaceEntry(archive, id, input.ReadContent());
    }
  }

  /// <inheritdoc cref="AppleSingleFormatDescriptor.Remove" />
  public void Remove(Stream archive, string[] entryNames)
    => this._shared.Remove(archive, entryNames);

  /// <summary>Entry id 1 — the data fork, which an AppleDouble sidecar by definition does not hold.</summary>
  private const uint DataForkEntryId = 1;
}
