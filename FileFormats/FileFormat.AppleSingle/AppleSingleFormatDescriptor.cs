#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AppleSingle;

/// <summary>
/// Pseudo-archive descriptor for AppleSingle (RFC 1740) container files. Each
/// entry id (data fork, resource fork, Finder info, dates, real name, …) is
/// surfaced as a separate archive entry plus a metadata.ini summary.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc1740</c> — RFC 1740 — carries the AppleSingle/AppleDouble format description as an appendix</description></item>
///   <item><description>Apple "AppleSingle/AppleDouble Formats for Foreign Files Developer's Note" (1990) — the defining vendor document</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/AppleSingle_and_AppleDouble_formats</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class AppleSingleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "AppleSingle";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "AppleSingle";
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
  public string DefaultExtension => ".as";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".as", ".applesingle"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x05, 0x16, 0x00], Confidence: 0.90),
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
    "AppleSingle (RFC 1740) container — bundles data fork, resource fork, " +
    "Finder info, and Mac metadata for transport across non-HFS filesystems. " +
    "R/W: in-place Add / Replace / Remove against the 12-byte entry directory.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored",
      false, false, null)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Opens a single AppleSingle entry as a bounded read-only stream. Each
  /// entry's decoded byte buffer is wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IArchiveCreatable ─────────────────────────────────────────────

  /// <summary>
  /// Emits a fresh AppleSingle container from the supplied inputs. Input
  /// archive names are mapped to entry ids via
  /// <see cref="AppleSingleWriter.EntryIdForName"/>; the synthetic
  /// <c>metadata.ini</c> entry the descriptor surfaces on read is silently
  /// dropped during create — it isn't a real AppleSingle entry.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var entries = new List<(uint EntryId, byte[] Data)>(inputs.Count);
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      entries.Add((AppleSingleWriter.EntryIdForName(Path.GetFileName(name)), data));
    }
    var bytes = AppleSingleWriter.Build(entries);
    output.Position = 0;
    output.Write(bytes, 0, bytes.Length);
    output.SetLength(bytes.Length);
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by id) entries inside an existing AppleSingle
  /// container. Routes through <see cref="AppleSingleInPlaceModifier"/> so
  /// untouched payload byte-content survives the operation.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      var id = AppleSingleWriter.EntryIdForName(Path.GetFileName(name));
      AppleSingleInPlaceModifier.ReplaceEntry(archive, id, data);
    }
  }

  /// <summary>
  /// Removes named entries from an existing AppleSingle container. Routes
  /// through <see cref="AppleSingleInPlaceModifier"/> — payload bytes are
  /// zero-wiped and the 12-byte directory slot is compacted out.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      var id = AppleSingleWriter.EntryIdForName(Path.GetFileName(name));
      AppleSingleInPlaceModifier.RemoveEntry(archive, id);
    }
  }

  private static IEnumerable<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var container = AppleSingleReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    yield return ("metadata.ini", BuildMetadata(container));
    foreach (var e in container.Entries)
      yield return (e.Name, e.Data);
  }

  private static byte[] BuildMetadata(AppleSingleReader.Container c) {
    var sb = new StringBuilder();
    sb.AppendLine("[applesingle]");
    sb.Append(CultureInfo.InvariantCulture, $"format = {(c.IsDouble ? "AppleDouble" : "AppleSingle")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version = 0x{c.Version:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"entry_count = {c.Entries.Count}\n");
    foreach (var e in c.Entries)
      sb.Append(CultureInfo.InvariantCulture, $"entry_{e.EntryId:D2} = {AppleSingleReader.EntryDescription(e.EntryId)} ({e.Data.Length} bytes)\n");
    var realName = c.Entries.FirstOrDefault(e => e.EntryId == 3);
    if (realName != null)
      sb.Append(CultureInfo.InvariantCulture, $"real_name = {AppleSingleReader.DecodeRealName(realName.Data)}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
