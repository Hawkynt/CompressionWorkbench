#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Deb;

/// <summary>
/// Debian binary package (.deb) — an ar archive holding debian-binary, control.tar.* and data.tar.*.
///
/// References:
/// <list type="bullet">
///   <item><description><c>deb(5)</c> man page (dpkg) — the authoritative format description</description></item>
///   <item><description><c>https://www.debian.org/doc/debian-policy/</c> — Debian Policy Manual — binary package requirements</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Deb_(file_format)</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class DebFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <summary>Rebuild-based defrag: extracts the data.tar entries and rebuilds the .deb package.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts the data.tar entries and rebuilds the .deb package.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new DebReader(stream);
        return r.ReadDataEntries().Where(e => !e.IsDirectory).Select(e => (e.Path, e.Data));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var control = new DebEntry("control",
          "Package: pkg\nVersion: 1.0\nArchitecture: all\nDescription: rebuilt by CompressionWorkbench\n"u8.ToArray(), false);
        var dataFiles = files.Select(f => new DebEntry(f.Name, f.Data, false)).ToList();
        var w = new DebWriter(ms);
        w.Write([control], dataFiles);
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    // Deb is an ar archive; delegate to the ar layout walker.
    archive.Position = 0;
    yield return new DefragBlockInfo(0, Ar.ArConstants.GlobalHeaderSize, DefragBlockKind.MetadataReserved, FileName: "AR Global Header");
    var r = new Ar.ArReader(archive);
    long pos = Ar.ArConstants.GlobalHeaderSize;
    foreach (var e in r.Entries) {
      yield return new DefragBlockInfo(pos, Ar.ArConstants.EntryHeaderSize, DefragBlockKind.MetadataReserved, FileName: "Header: " + e.Name);
      pos += Ar.ArConstants.EntryHeaderSize;
      if (e.Data.Length > 0)
        yield return new DefragBlockInfo(pos, e.Data.Length, DefragBlockKind.Used, FileName: e.Name);
      pos += e.Data.Length;
      if (e.Data.Length % 2 != 0)
        pos++;
    }
  }

  public string Id => "Deb";
  public string DisplayName => "DEB";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".deb";
  public IReadOnlyList<string> Extensions => [".deb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deb", "DEB")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Debian package archive (ar + tar.gz/xz)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DebReader(stream);
    var data = r.ReadDataEntries();
    return data.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Data.Length, e.Data.Length,
      "deb", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DebReader(stream);
    foreach (var e in r.ReadDataEntries()) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Path)); continue; }
      WriteFile(outputDir, e.Path, e.Data);
    }
  }

  /// <summary>
  /// Opens a single DEB entry as a bounded read-only stream. DEB stores
  /// its payload inside an inner compressed <c>data.tar.*</c> (gz/xz/zst/bz2);
  /// the reader already materialises each entry's bytes during enumeration,
  /// so the override wraps the matched entry's bytes in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length — block padding and adjacent entries cannot leak.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new DebReader(archive);
    foreach (var e in r.ReadDataEntries()) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Path, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var control = new DebEntry("control", "Package: pkg\nVersion: 1.0\nArchitecture: all\nDescription: created by CompressionWorkbench\n"u8.ToArray(), false);
    var dataFiles = FormatHelpers.FilesOnly(inputs)
      .Select(f => new DebEntry(f.Name, f.Data, false))
      .ToList();
    var w = new DebWriter(output);
    w.Write([control], dataFiles);
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
