#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pbp;

/// <summary>
/// Sony PSP PBP package (EBOOT.PBP) — 'PBP' magic plus eight offsets to PARAM.SFO, icon/PIC/PMF media and DATA.PSP / DATA.PSAR sections.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/pspdev/pspsdk</c> — PSP homebrew SDK — de-facto reference for the PBP header</description></item>
///   <item><description>No official Sony specification — structure documented by the PSP homebrew community</description></item>
/// </list>
/// </summary>
public sealed class PbpFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "PBP is a PSP firmware/EBOOT container with fixed-section layout (ICON0/PIC0/PIC1/SND0/PSP/PSAR) — " +
      "section order is part of the format spec; defragmentation isn't meaningful.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new PbpReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Pbp";
  public string DisplayName => "PSP PBP Archive";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pbp";
  public IReadOnlyList<string> Extensions => [".pbp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0x00, 0x50, 0x42, 0x50 }, Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pbp", "PBP")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PlayStation Portable EBOOT/multi-section archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PbpReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PbpReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
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
    var r = new PbpReader(archive);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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
    using var w = new PbpWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
  }
}
