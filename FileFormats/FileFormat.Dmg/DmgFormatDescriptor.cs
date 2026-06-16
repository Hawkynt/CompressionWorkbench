#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dmg;

public sealed class DmgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "DMG is an Apple disk image with mish blocks and a signed footer — defragmentation isn't meaningful.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  public string Id => "Dmg";
  public string DisplayName => "DMG";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dmg";
  public IReadOnlyList<string> Extensions => [".dmg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dmg", "DMG")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DmgReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, stream.Length,
      "DMG", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DmgReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single DMG partition as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor reconstructs the partition's raw
  /// sectors; they are wrapped in a <see cref="BoundedEntryStream"/> sized
  /// to the entry's size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new DmgReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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
    // WORM: each input becomes a partition with a single raw mish block (no
    // compression). The reader rebuilds sectors from the raw block and writes
    // each partition out at extract time.
    var w = new DmgWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddPartition(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }
}
