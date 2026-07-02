#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Msi;

/// <summary>
/// Microsoft OLE2 Compound File Binary container (MSI installer databases, legacy Office documents).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/</c> — [MS-CFB] Compound File Binary File Format — Microsoft Open Specifications</description></item>
///   <item><description><c>https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-portal</c> — Windows Installer documentation portal</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Compound_File_Binary_Format</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class MsiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "MSI is an OLE2 Compound File envelope with Installer DB schema tables, transforms, and cabinet streams — " +
      "rebuilding from the surface stream list would destroy the package structure.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => CfbLayoutMap.Enumerate(archive);

  public string Id => "Msi";
  public string DisplayName => "MSI (OLE Compound File)";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".msi";
  public IReadOnlyList<string> Extensions => [".msi", ".msp", ".mst"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft OLE Compound File (MSI, legacy Office documents)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MsiReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullPath, e.Size, e.Size, "Stored",
      e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MsiReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single MSI / OLE2 stream as a bounded read-only <see cref="Stream"/>.
  /// The CFB reader's per-entry extract returns the stream's raw bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new MsiReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FullPath, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: produces a structurally-valid CFB file. NOT a functional Windows
    // Installer package (that requires Installer DB schema tables, transforms,
    // cabinet streams, etc. — well out of scope). Useful for CFB container
    // round-trip and for tools that need to pack streams into an OLE envelope.
    var w = new CfbWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var leaf = Path.GetFileName(i.ArchiveName);
      if (string.IsNullOrEmpty(leaf)) leaf = i.ArchiveName;
      if (leaf.Length > 31) leaf = leaf[..31];
      w.AddStream(leaf, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Zeros every dead byte in the archive: any byte not covered by a live extent
  /// in the layout map (headers, entry data and directory structures are live and
  /// preserved, so the archive still lists and extracts identically). Cluster-tip
  /// wiping is N/A (entries are stored byte-exact with no per-file slack).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = this.EnumerateLayout(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
