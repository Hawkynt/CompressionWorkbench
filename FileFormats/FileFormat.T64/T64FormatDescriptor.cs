#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.T64;

/// <summary>
/// Commodore 64 T64 tape container — directory of memory-load records.
///
/// References:
/// <list type="bullet">
///   <item><description>Peter Schepers, "C64 File Formats: T64" — the classic reference document</description></item>
///   <item><description><c>https://vice-emu.sourceforge.io/</c> — VICE emulator — reference implementation reading/writing T64</description></item>
/// </list>
/// </summary>
public sealed class T64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemBlockMover {
  public string Id => "T64";
  public string DisplayName => "T64";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".t64";
  public IReadOnlyList<string> Extensions => [".t64"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("C64"u8.ToArray(), Confidence: 0.70)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 64 tape container";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new T64Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new T64Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new T64Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);
    output.Write(w.Build());
  }

  // ── IArchiveModifiable (in-place) ─────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing T64 tape image via
  /// <see cref="T64InPlaceModifier"/>. If a directory slot is free the entry
  /// drops in directly and the new payload is appended at EOF. If the
  /// directory is full the directory grows by one 32-byte slot — the payload
  /// region shifts forward by 32 bytes and every existing slot's absolute
  /// dataOffset field is patched. No full image rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs))
      T64InPlaceModifier.AddFile(archive, name.Length > 16 ? name[..16] : name, data);
  }

  /// <summary>
  /// Removes named entries from an existing T64 tape image via
  /// <see cref="T64InPlaceModifier"/>. Later directory slots shift up by 32
  /// bytes, the removed payload bytes are wiped, the remaining payload region
  /// shifts to close the gap (each affected slot's absolute dataOffset is
  /// patched), and the stream is truncated.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      T64InPlaceModifier.RemoveFile(archive, name);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new T64BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new T64BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  // ── IArchiveDefragmentable ───────────────────────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments a T64 image. Falls back to rebuild since T64 data offsets
  /// are stored in directory entries and recompaction is simplest via rebuild.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveLayoutMap ────────────────────────────────────────────────

  /// <summary>
  /// Enumerates the byte layout of a T64 tape image: 64-byte header as
  /// MetadataReserved, N×32-byte directory entries as MetadataReserved,
  /// and each file's data region as Used.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new T64Reader(archive);

    // Header: 64 bytes
    const int headerSize = 64;
    const int entrySize = 32;
    var dirSize = r.Entries.Count * entrySize;

    yield return new DefragBlockInfo(0, headerSize, DefragBlockKind.MetadataReserved, "T64 Header");
    if (dirSize > 0)
      yield return new DefragBlockInfo(headerSize, dirSize, DefragBlockKind.MetadataReserved, "Directory");

    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.Size, DefragBlockKind.Used, e.Name);
    }

    var imageSize = archive.Length;
    var dataEnd = r.Entries.Count > 0
      ? r.Entries.Max(e => e.DataOffset + e.Size)
      : headerSize + dirSize;
    if (dataEnd < imageSize)
      yield return new DefragBlockInfo(dataEnd, imageSize - dataEnd, DefragBlockKind.Free);
  }

  // ── Shared delegates ─────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new T64Reader(stream);
    return r.Entries.Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new T64Writer();
    foreach (var (n, d) in files)
      w.AddFile(n.Length > 16 ? n[..16] : n, d);
    return w.Build();
  }
}
