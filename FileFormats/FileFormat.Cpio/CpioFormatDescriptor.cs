#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cpio;

/// <summary>
/// cpio archive — Unix copy-in/copy-out container (binary, portable-ASCII odc and newc variants).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html</c> — POSIX pax — defines the cpio interchange headers</description></item>
///   <item><description><c>cpio(5)</c> man page (libarchive / FreeBSD) — documents the binary, odc, newc and crc variants</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Cpio</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class CpioFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the CPIO archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the CPIO archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new CpioReader(stream);
        return r.ReadAll().Where(x => !x.Entry.IsDirectory).Select(x => (x.Entry.Name, x.Data));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new CpioWriter(ms);
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.Finish();
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    using var r = new CpioReader(archive, leaveOpen: true);
    var entries = r.ReadAll();
    // Walk the stream sequentially to compute entry positions
    archive.Position = 0;
    foreach (var (entry, data) in entries) {
      var nameSize = System.Text.Encoding.ASCII.GetByteCount(entry.Name) + 1;
      var headerPlusName = CpioConstants.NewAsciiHeaderSize + nameSize;
      var namePadding = (4 - headerPlusName % 4) % 4;
      var totalHeader = headerPlusName + namePadding;
      var pos = archive.Position;
      yield return new DefragBlockInfo(pos, totalHeader, DefragBlockKind.MetadataReserved, FileName: $"Header: {entry.Name}");
      if (entry.FileSize > 0) {
        yield return new DefragBlockInfo(pos + totalHeader, entry.FileSize, DefragBlockKind.Used, FileName: entry.Name);
        var dataPadding = (4 - entry.FileSize % 4) % 4;
        archive.Position = pos + totalHeader + entry.FileSize + dataPadding;
      } else {
        archive.Position = pos + totalHeader;
      }
    }
  }

  public string Id => "Cpio";
  public string DisplayName => "CPIO";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>Adds (or replaces by name) files via <see cref="CpioModifier"/>.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      CpioModifier.RemoveFile(archive, name, wipeData: true);
      CpioModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries via <see cref="CpioModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      CpioModifier.RemoveFile(archive, name, wipeData: true);
  }

  public string DefaultExtension => ".cpio";
  public IReadOnlyList<string> Extensions => [".cpio"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xC7, 0x71], Confidence: 0.90),
    new([(byte)'0', (byte)'7', (byte)'0', (byte)'7', (byte)'0', (byte)'7'], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cpio", "CPIO")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix copy-in/copy-out archive format";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CpioReader(stream);
    var all = r.ReadAll();
    return all.Select((x, i) => new ArchiveEntryInfo(i, x.Entry.Name, x.Entry.FileSize, x.Entry.FileSize,
      "cpio", x.Entry.IsDirectory, false, DateTimeOffset.FromUnixTimeSeconds(x.Entry.ModificationTime).DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CpioReader(stream);
    foreach (var (entry, data) in r.ReadAll()) {
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      if (entry.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, entry.Name)); continue; }
      WriteFile(outputDir, entry.Name, data);
    }
  }

  /// <summary>
  /// Opens a single CPIO entry as a bounded read-only <see cref="Stream"/>.
  /// CPIO stores each entry uncompressed; the reader's <c>ReadAll</c> walk
  /// surfaces (entry, byte[]) tuples which the bounded wrapper sizes to the
  /// entry's file size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new CpioReader(archive, leaveOpen: true);
    foreach (var (entry, data) in r.ReadAll()) {
      if (entry.IsDirectory) continue;
      if (!string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new BoundedEntryStream(new MemoryStream(data, writable: false),
        data.Length, leaveOpen: false);
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
    var w = new CpioWriter(output);
    foreach (var i in inputs) {
      if (i.IsDirectory) w.AddDirectory(i.ArchiveName);
      else w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.Finish();
  }

  /// <summary>
  /// Large-file-safe streaming variant of <see cref="Create"/>. The cpio
  /// "new" ASCII header encodes each member's size before its payload, so the
  /// pre-known <see cref="StreamingArchiveInput.Size"/> lets the writer emit
  /// the header and then copy the payload in 64 KB chunks via
  /// <see cref="CpioWriter.AddStreamingFile"/> — peak memory is bounded by the
  /// copy buffer regardless of member size. Inode allocation, headers, and
  /// padding match <see cref="Create"/> byte-for-byte for the same inputs.
  /// </summary>
  public void CreateFromStreams(Stream target, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new CpioWriter(target);
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.Name); continue; }
      using var src = i.OpenStream();
      w.AddStreamingFile(i.Name, i.Size, src);
    }
    w.Finish();
  }
}
