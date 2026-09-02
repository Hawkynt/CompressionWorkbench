#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.VxFs;

/// <summary>
/// Read-only descriptor for VxFS (Veritas File System), used by HP-UX,
/// Solaris, and AIX (and a Linux read-only port). Walking the OLT (Object
/// Location Table) → FSH (FileSet Header) → IAU (Inode Allocation Unit)
/// chain to extract user files is explicitly out of scope (multi-week
/// effort) — this descriptor surfaces:
/// <list type="bullet">
///   <item><description><c>FULL.vxfs</c> — the raw image bytes</description></item>
///   <item><description><c>metadata.ini</c> — parsed superblock fields</description></item>
///   <item><description><c>superblock.bin</c> — 1 KB capture of the on-disk superblock</description></item>
/// </list>
///
/// Detection: 4-byte magic <c>0xA501FCF5</c> at offset 1024. The magic is
/// stored in the natural endianness of the host that wrote the volume —
/// little-endian on x86 / Linux, big-endian on HP-UX PA-RISC and Solaris
/// SPARC. Both signature variants are registered.
///
/// Create / Modify / Defragment: <see cref="NotSupportedException"/> — the
/// descriptor is read-only.
///
/// References:
/// <list type="bullet">
///   <item><description>Linux kernel <c>fs/freevxfs/vxfs.h</c> + <c>vxfs_super.c</c></description></item>
///   <item><description>HP-UX "VxFS Administrator's Guide" (Veritas / Symantec)</description></item>
///   <item><description>Wikipedia "Veritas File System"</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>The walk to the files is implemented in <see cref="VxFsVolume" /> and
/// the volumes this writes are mounted by the kernel's own <c>freevxfs</c>
/// driver, so the superblock surface above is no longer all there is: files are
/// listed, extracted, written and laid out again.</para>
///
/// <para>What is written is the plainest shape the driver accepts — one fileset,
/// direct extents only, a flat root directory. Immediate data, extent trees and
/// subdirectories are shapes it reads and this does not write.</para>
/// </remarks>
public sealed class VxFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IFilesystemExtentMap {

  /// <summary>Entries that describe the volume rather than live in it.</summary>
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.Ordinal) { "FULL.vxfs", "metadata.ini", "superblock.bin" };
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "VxFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "VxFS (Veritas)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest
    | FormatCapabilities.CanCreate;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".vxfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".vxfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Little-endian (x86 / Linux native) — vs_magic = 0xA501FCF5 → F5 FC 01 A5.
    new(VxFsReader.MagicLE, Offset: VxFsReader.SuperblockOffset, Confidence: 0.90f),
    // Big-endian (HP-UX PA-RISC / Solaris SPARC native) — A5 01 FC F5.
    new(VxFsReader.MagicBE, Offset: VxFsReader.SuperblockOffset, Confidence: 0.90f),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "VxFS (Veritas File System) volume — files, and a layout pass over them.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.vxfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    VxFsReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new VxFsReader(ms);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.vxfs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.vxfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (reader.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "superblock.bin", reader.HeaderRaw.LongLength, reader.HeaderRaw.LongLength, "stored", false, false, null));

    // And the files themselves, when the walk to them lands.
    using (var full = new MemoryStream(image, writable: false)) {
      var volume = new VxFsVolume(full);
      if (volume.Valid)
        foreach (var file in volume.Files)
          entries.Add(new ArchiveEntryInfo(idx++, file.Name, file.Size, file.Size, "stored", false, false, null));
    }

    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    VxFsReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new VxFsReader(ms);
    } catch {
      WriteIfMatch(outputDir, "FULL.vxfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.vxfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(reader), files);
    if (reader.Valid)
      WriteIfMatch(outputDir, "superblock.bin", reader.HeaderRaw, files);

    using var full = new MemoryStream(image, writable: false);
    var volume = new VxFsVolume(full);
    if (!volume.Valid) return;
    foreach (var file in volume.Files)
      WriteIfMatch(outputDir, file.Name, volume.Read(file), files);
  }

  /// <summary>Writes a volume the Veritas driver mounts, holding the given files.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var writer = new VxFsWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var data = input.InMemoryContent ?? File.ReadAllBytes(input.FullPath);
      writer.AddFile(input.ArchiveName, data);
    }

    var image = writer.Build();
    output.Write(image, 0, image.Length);
    output.Flush();
  }

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) => VxFsExtentMap.Enumerate(image);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Moves the blocks that are out of place and repoints the inodes.</summary>
  /// <remarks>
  /// <para>A file's blocks are named by direct extents inside its own inode, so
  /// a move is a copy and a rewritten pair of numbers. Everything the driver
  /// walks on its way to the files — the superblock, the object location table,
  /// the raw inode array, the fileset headers, both inode lists and the root
  /// directory — is off limits, because a volume with a file on top of any of
  /// them stops being mountable.</para>
  ///
  /// <para>The inodes are written back once the pass is over. One run's old
  /// home is routinely another's new one, and an inode rewritten halfway
  /// through would describe a layout that no longer holds.</para>
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    if (!archive.CanSeek || archive.Length > PlannerImageCap)
      throw new NotSupportedException(
        "VxFS defragmentation needs a seekable volume small enough to verify by reading it back.");

    var planned = false;
    // The pass is kept only if every file still reads back: a mover can refuse
    // partway, and leaving the volume as it was is the honest answer when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: ReadPayloadsForGuard,
      inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
      rebuild: () => planned = false);

    if (!planned)
      throw new NotSupportedException(
        "VxFS defragmentation could not lay this volume out in place, and there is no rebuild to " +
        "fall back on: a file's blocks must stay clear of the structures the driver walks.");
  }

  /// <summary>Largest volume held in memory twice for the guarded pass.</summary>
  private const long PlannerImageCap = 256L * 1024 * 1024;

  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var volume = new VxFsVolume(stream);
    if (!volume.Valid)
      throw new InvalidDataException($"VxFS: {volume.Status}.");
    return volume.Files.Select(volume.Read).ToList();
  }

  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    var mover = new VxFsBlockMover();
    archive.Position = 0;
    mover.Init(archive);

    archive.Position = 0;
    var extents = VxFsExtentMap.Enumerate(archive).ToList();
    if (extents.Count == 0) return;

    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);
    mover.Settle(archive);

    archive.Position = 0;
    var postExtents = VxFsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(VxFsReader r) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={r.ParseStatus}\n");
    if (r.Valid) {
      b.Append(ic, $"endianness={(r.IsBigEndian ? "big" : "little")}\n");
      b.Append(ic, $"vs_magic=0x{r.VsMagic:X8}\n");
      b.Append(ic, $"vs_version={r.VsVersion}\n");
      b.Append(ic, $"vs_mtime={r.VsMtime}\n");
      b.Append(ic, $"vs_ctime={r.VsCtime}\n");
      b.Append(ic, $"vs_blocksize={r.VsBlockSize}\n");
      b.Append(ic, $"vs_size={r.VsSize}\n");
      b.Append(ic, $"vs_dsize={r.VsDsize}\n");
      b.Append(ic, $"vs_old_nau={r.VsOldNau}\n");
      b.Append(ic, $"vs_immedlen={r.VsImmedLen}\n");
      b.Append(ic, $"vs_ndaddr={r.VsNdAddr}\n");
      b.Append(ic, $"vs_firstau={r.VsFirstAu}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  /// <summary>
  /// Reads the volume, up to the same ceiling the layout pass works under.
  /// </summary>
  /// <remarks>
  /// This used to stop after 64 KiB, which was enough while the only thing read
  /// was the superblock at offset 1024. It is not enough now: the files live
  /// wherever their extents say, and a prefix of a volume lists the ones near
  /// the front and truncates the rest — including <c>FULL.vxfs</c>, which is
  /// supposed to be the image itself. The cap remains so a speculative carver
  /// scan cannot pull an unbounded image into memory.
  /// </remarks>
  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[64 * 1024];
    int read;
    while (ms.Length < PlannerImageCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
