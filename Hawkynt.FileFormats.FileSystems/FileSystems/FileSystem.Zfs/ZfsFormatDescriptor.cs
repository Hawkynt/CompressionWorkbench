using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Zfs;

/// <summary>
/// Descriptor for ZFS pool device images (vdevs) as OpenZFS writes them. Read-only:
/// every dataset and snapshot, all ZFS block compressions, gang and embedded blocks,
/// mirrors and RAID-Z (see <see cref="ZfsReader"/>).
///
/// References:
/// <list type="bullet">
///   <item><description>Sun Microsystems, "ZFS On-Disk Specification" (2006 draft) — vdev labels, uberblocks, DMU, DSL, ZAP, ZPL</description></item>
///   <item><description><c>docs/ZFS-ON-DISK.md</c> — the structures added since, derived from OpenZFS 2.2</description></item>
///   <item><description><c>https://github.com/openzfs/zfs</c> — OpenZFS, whose tools wrote the reference pools the tests read</description></item>
///   <item><description><c>https://openzfs.github.io/openzfs-docs/</c> — OpenZFS documentation</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Create writes a new single-device pool (version 28, one ZPL version 3 filesystem holding the
/// inputs) with <see cref="ZfsWriter"/>; OpenZFS's <c>zdb -bcc</c> finds no leaks in it and the
/// kernel module imports, scrubs and mounts it. Modifying pools in place is not offered.
/// </remarks>
public sealed class ZfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <summary>Gets the id.</summary>
  public string Id => "Zfs";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "ZFS";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".zfs";
  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".zfs", ".zpool"];
  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // A vdev label has no signature at offset 0 (the first 8 KiB are blank). The uberblock
  // ring starts 128 KiB in, and its first slot usually begins with UBERBLOCK_MAGIC.
  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x0C, 0xB1, 0xBA, 0x00, 0x00, 0x00, 0x00, 0x00],
      Offset: ZfsConstants.UberblockRingOffset, Confidence: 0.9),
  ];
  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zfs", "ZFS")];
  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Gets the description.</summary>
  public string Description =>
    "ZFS pool device image — reads every dataset and snapshot; lzjb, zle, gzip, lz4 and zstd blocks; " +
    "gang and embedded blocks, ditto copies, mirror and RAID-Z. Creates single-device version 28 pools " +
    "that OpenZFS imports and scrubs clean.";

  private const string FullName = "FULL.zfs";
  private const string MetadataName = "metadata.ini";

  /// <summary>
  /// Lists the pool's files. Never throws: an image that cannot be read lists as
  /// <c>FULL.zfs</c> plus a <c>metadata.ini</c> saying why.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      using var reader = new ZfsReader(stream);
      var entries = reader.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "zfs", e.IsDirectory, false, e.LastModified,
        IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget)).ToList();
      entries.Add(new ArchiveEntryInfo(entries.Count, MetadataName, BuildMetadata(reader).LongLength, 0,
        "stored", false, false, null));
      return entries;
    } catch (Exception e) when (IsReadFailure(e)) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [
        new ArchiveEntryInfo(0, FullName, length, length, "stored", false, false, null),
        new ArchiveEntryInfo(1, MetadataName, 0, 0, "stored", false, false, null),
      ];
    }
  }

  /// <summary>Extracts the pool's files (links as files holding their target).</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ZfsReader reader;
    try {
      reader = new ZfsReader(stream);
    } catch (Exception e) when (IsReadFailure(e)) {
      if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
        WriteFile(outputDir, MetadataName, Encoding.UTF8.GetBytes($"parse_status=partial\nerror={OneLine(e.Message)}\n"));
      if (files is not { Length: > 0 } || MatchesFilter(FullName, files)) {
        stream.Position = 0;
        using var full = CreateEntryFile(outputDir, FullName);
        stream.CopyTo(full);
      }
      return;
    }

    using (reader) {
      foreach (var e in reader.Entries) {
        if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
        if (e.IsDirectory) {
          Directory.CreateDirectory(Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar)));
          continue;
        }
        using var output = CreateEntryFile(outputDir, e.Name);
        reader.ExtractTo(e, output);
      }
      if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
        WriteFile(outputDir, MetadataName, BuildMetadata(reader));
    }
  }

  /// <summary>
  /// Writes a new single-device pool (version 28) whose one filesystem holds <paramref name="inputs"/>.
  /// The option <c>PoolName</c> names the pool (default <c>cwb</c>).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new ZfsWriter();
    foreach (var input in inputs) {
      var modified = input.Metadata?.LastWriteTimeUtc?.UtcDateTime;
      if (input.IsDirectory)
        writer.AddDirectory(input.ArchiveName, modified);
      else
        writer.AddFile(input.ArchiveName, input.ReadContent(), modified);
    }
    writer.Build(output, options.GetOption("PoolName", "cwb"));
  }

  private static byte[] BuildMetadata(ZfsReader reader) {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(reader.Warnings.Count == 0 ? "ok" : "partial").Append('\n');
    b.Append("pool=").Append(reader.PoolName).Append('\n');
    b.Append("pool_guid=").Append(reader.PoolGuid).Append('\n');
    b.Append("version=").Append(reader.PoolVersion).Append('\n');
    b.Append("txg=").Append(reader.Txg).Append('\n');
    b.Append("vdevs=").Append(string.Join("; ", reader.Topology)).Append('\n');
    foreach (var d in reader.Datasets)
      b.Append(d.Refusal == null ? "dataset=" : "refused=").Append(d.Name)
        .Append(d.Refusal == null ? "" : " (" + d.Refusal + ")").Append('\n');
    if (reader.UnverifiedChecksums.Count > 0)
      b.Append("unverified_checksums=").Append(string.Join(",", reader.UnverifiedChecksums.Order(StringComparer.Ordinal))).Append('\n');
    foreach (var w in reader.Warnings)
      b.Append("warning=").Append(OneLine(w)).Append('\n');
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  private static string OneLine(string s) => s.ReplaceLineEndings(" ");

  private static bool IsReadFailure(Exception e)
    => e is InvalidDataException or NotSupportedException or IOException or ArgumentException
         or IndexOutOfRangeException or OverflowException;
}
