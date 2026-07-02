#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.InnoSetup;

/// <summary>
/// Inno Setup installer package (PE stub + Setup.0 data blob).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://jrsoftware.org/isinfo.php</c> — official Inno Setup site (Jordan Russell)</description></item>
///   <item><description><c>https://github.com/dscharrer/innoextract</c> — innoextract — de-facto reference for the undocumented installer data layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Inno_Setup</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class InnoSetupFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {
  public string Id => "InnoSetup";
  public string DisplayName => "Inno Setup";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("innosetup", "Inno Setup")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Inno Setup installer archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new InnoSetupReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, e.CompressedSize,
      "innosetup", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new InnoSetupReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The reader produces
  /// the decoded bytes per entry; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to their logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new InnoSetupReader(archive);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
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
    byte[]? embedded = null;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      embedded = i.ReadContent();
      break;
    }
    new InnoSetupWriter().WriteTo(output, embedded);
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "Inno Setup is a single-payload installer wrapper (PE stub + opaque Setup.0 blob) — " +
      "defragmentation is not meaningful and would destroy the installer's signed structure.");

  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);
}
