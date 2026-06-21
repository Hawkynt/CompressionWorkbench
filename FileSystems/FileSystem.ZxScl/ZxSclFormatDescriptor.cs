#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ZxScl;

public sealed class ZxSclFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  // Upper bound: max payload (40 tracks x 16 sectors x 256 bytes x 4 layers) + magic/headers/CRC.
  public long? MaxTotalArchiveSize => ZxSclReader.MaxPayloadSize;
  public string AcceptedInputsDescription =>
    "ZX Spectrum TR-DOS file (up to 655 360 bytes total; 8-char names).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>
  /// SCL is variable-size — there's no fixed canonical byte count. We declare the hard
  /// payload ceiling so <see cref="IArchiveShrinkable"/>-style consumers still have a target.
  /// </summary>
  public IReadOnlyList<long> CanonicalSizes => [];

  public string Id => "ZxScl";
  public string DisplayName => "SCL (ZX Spectrum)";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ZxScl archive via
  /// <see cref="ZxSclInPlaceModifier"/>. Each file is inserted with a single
  /// 14-byte right-shift of the payload region followed by an entry-header
  /// write and a sector-padded data append — no full image rebuild.
  /// Replacement of an existing same-named entry is handled by a prior
  /// in-place remove. SCL has no compression or random-access map so the
  /// trailing 32-bit checksum is recomputed once per mutation.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs))
      ZxSclInPlaceModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing ZxScl image via
  /// <see cref="ZxSclInPlaceModifier"/>. Later directory entries shift up by
  /// 14 bytes, the trailing payload region shifts back to close the gap, the
  /// stream is truncated and the trailing checksum is recomputed.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      ZxSclInPlaceModifier.RemoveFile(archive, name);
  }


  public string DefaultExtension => ".scl";
  public IReadOnlyList<string> Extensions => [".scl"];
  public IReadOnlyList<string> CompoundExtensions => [];

  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(ZxSclReader.Magic, Offset: 0, Confidence: 0.95)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZX Spectrum SCL archive (TR-DOS compact form)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ZxSclReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ZxSclReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += i.InMemoryContent?.LongLength ?? new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"SCL: combined input size {total} bytes exceeds TR-DOS payload ceiling ({cap} bytes).");

    var w = new ZxSclWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IArchiveDefragmentable (rebuild-based) ───────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveLayoutMap ────────────────────────────────────────────────

  /// <summary>
  /// Enumerates the byte layout of an SCL archive: 8-byte magic as
  /// MetadataReserved, 1-byte file count + N×14-byte headers as
  /// MetadataReserved, each file's sector-padded data region as Used,
  /// and the trailing 4-byte CRC as MetadataReserved.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    using var r = new ZxSclReader(archive);

    // Magic: 8 bytes
    yield return new DefragBlockInfo(0, 8, DefragBlockKind.MetadataReserved, "SINCLAIR magic");

    // File count (1 byte) + N × 14-byte headers
    var headerTableSize = 1 + r.Entries.Count * ZxSclReader.HeaderSize;
    yield return new DefragBlockInfo(8, headerTableSize, DefragBlockKind.MetadataReserved, "Directory");

    // File data regions
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.Size, DefragBlockKind.Used, e.Name);
    }

    // Trailing CRC: 4 bytes at end
    var crcOffset = archive.Length - 4;
    if (crcOffset > 0)
      yield return new DefragBlockInfo(crcOffset, 4, DefragBlockKind.MetadataReserved, "CRC32");
  }

  // ── Shared delegates ─────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ZxSclReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new ZxSclWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }
}
