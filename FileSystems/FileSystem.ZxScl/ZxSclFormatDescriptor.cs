#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ZxScl;

public sealed class ZxSclFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

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
  /// Adds (or replaces by name) files inside an existing ZxScl archive.
  /// Read-extract-rebuild via <c>ModifyRebuilder</c>. SCL is a flat stream
  /// archive (8-byte magic + 1-byte count + variable-size header table +
  /// concatenated payloads + 4-byte CRC) without sector geometry or a free
  /// map, so adding or removing a file shifts the entire payload region —
  /// there is no random-access O(touched bytes) path here. The rebuild
  /// path is the architecturally correct shape for this format and doubles
  /// as a secure wipe for replaced bytes. SCL payloads are bounded at
  /// 640 KB, so the rebuild is fast in absolute terms.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new ZxSclReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new ZxSclWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  /// <summary>
  /// Removes the named entries from an existing ZxScl image. The image is
  /// rebuilt without the target entries — old file bytes are wiped because
  /// the new layout starts fresh, leaving no forensic trace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new ZxSclReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new ZxSclWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });


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
    foreach (var i in inputs) if (!i.IsDirectory) total += new FileInfo(i.FullPath).Length;
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
