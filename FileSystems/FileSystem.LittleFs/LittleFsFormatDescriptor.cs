#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.LittleFs;

/// <summary>
/// Read-only descriptor for LittleFS images (Arduino / RTOS / IoT embedded-flash FS).
/// Surfaces the superblock and parsed geometry. Walking the tag-based metadata
/// pair commit log with CRC validation is intentionally out of scope — that's a
/// full reference-implementation port. Detection + structural surfacing is the win.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/littlefs-project/littlefs</c> — canonical littlefs source (ARM Mbed lineage)</description></item>
///   <item><description><c>https://github.com/littlefs-project/littlefs/blob/master/SPEC.md</c> — on-disk format specification</description></item>
///   <item><description><c>https://github.com/littlefs-project/littlefs/blob/master/DESIGN.md</c> — design document (metadata pairs, CTZ skip-lists)</description></item>
/// </list>
/// </summary>
public sealed class LittleFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IArchiveCreatable, IFormatOptionsSchema, ILayoutOptimizable , IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The only writer-honoured knob is the block size: it is recorded in the
  /// littlefs superblock geometry (and bounds the inline-file threshold and CTZ
  /// block layout). LittleFS stores no volume label, so no label knob is published.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "BlockSize", displayName: "Block size",
      min: 128, max: 65536, defaultLabel: "4 KB",
      description: "Erase-block size recorded in the superblock. LittleFS allows powers of two from 128 B to 64 KB."),
  ];

  public string Id => "LittleFs";
  public string DisplayName => "LittleFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".littlefs";
  public IReadOnlyList<string> Extensions => [".littlefs", ".lfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Single canonical-offset registration. The reader's IndexOf scan inside
    // TryParse handles non-canonical placements (revision-byte width varies);
    // we only need ONE magic registration to surface the descriptor for
    // explicit-extension dispatch + the FilesystemCarver. Three duplicated
    // registrations triggered O(N) signature-scan candidate explosion in
    // FilesystemCarverTests.FatInsideRawDump_Detected on a 10 MB host buffer.
    // The name entry follows the revision count and the superblock tag, which
    // puts "littlefs" at +8 for the layout our writer emits; the +16 placement
    // is what a wider revision field gives. Both are registered because a
    // missing one let a one-byte 0.20-confidence signature win the image.
    new([0x6C, 0x69, 0x74, 0x74, 0x6C, 0x65, 0x66, 0x73], Offset: 8, Confidence: 0.6),
    new([0x6C, 0x69, 0x74, 0x74, 0x6C, 0x65, 0x66, 0x73], Offset: 16, Confidence: 0.6),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "LittleFS embedded-flash FS — metadata-pair walk + CTZ/inline file extraction.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();

    // Preferred path: walk the metadata-pair commit log and list the real files.
    // This reads through the stream, so it holds for a volume of any size.
    try {
      using var reader = new LittleFsReader(stream);
      if (reader.Files.Count > 0)
        return reader.Files.Select((f, i) => new ArchiveEntryInfo(
          i, f.Path, f.Size, f.Size, "stored", false, false, null)).ToList();
    } catch {
      // fall through to the superblock surface below
    }

    // Surface fallback for an image the walk cannot parse: only the header is
    // needed for it, so a large unparsable image still lists.
    byte[] image;
    try {
      image = ReadHeader(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.littlefs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }
    var imageLength = stream.CanSeek ? stream.Length : image.LongLength;

    LittleFsSuperblock sb;
    try {
      sb = LittleFsSuperblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.littlefs", imageLength, imageLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.littlefs", imageLength, imageLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength, sb.RawBytes.LongLength, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Preferred path: extract the real files via the commit-walking reader,
    // which streams both the walk and each file's blocks.
    try {
      using var reader = new LittleFsReader(stream);
      if (reader.Files.Count > 0) {
        foreach (var f in reader.Files) {
          if (files != null && files.Length > 0 && !MatchesFilter(f.Path, files))
            continue;
          var target = Path.Combine(outputDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
          Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
          using var output = File.Create(target);
          reader.ReadFileTo(f, output);
        }
        return;
      }
    } catch {
      // fall through to the superblock surface below
    }

    byte[] image;
    try {
      image = ReadHeader(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    LittleFsSuperblock sb;
    try {
      sb = LittleFsSuperblock.TryParse(image);
    } catch {
      WriteRawImage(outputDir, stream, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteRawImage(outputDir, stream, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
  }

  /// <summary>
  /// Builds a fresh littlefs image from <paramref name="inputs"/>. Files keep
  /// their archive-relative paths (forward-slash separated), so subdirectories
  /// are recreated as littlefs directory metadata pairs. Small files are stored
  /// inline; larger ones use CTZ skip-lists. The result round-trips through
  /// <see cref="LittleFsReader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new LittleFsWriter(ResolveBlockSize(options));
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddFile(input.ArchiveName, input.ReadContent());
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (genuine in-place) ──────────────────────────────

  /// <summary>
  /// Genuine in-place add/replace: rewrites only the inactive half of the root
  /// metadata pair with a fresh commit at <c>revision+1</c> and appends any new
  /// CTZ / subdirectory blocks past the current block count. The active root half
  /// and every existing data block stay byte-identical at their offsets — the
  /// littlefs metadata-pair ping-pong / copy-on-write model. See
  /// <see cref="LittleFsInPlaceModifier"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in — and where it can still edit, it has no room
    // to grow a full volume. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    LittleFsInPlaceModifier.Add(archive, inputs);
  }

  /// <summary>
  /// Genuine in-place remove: drops the named entries and rewrites the inactive
  /// root half at <c>revision+1</c>. Existing live blocks stay byte-identical;
  /// the removed file's data blocks are simply no longer referenced.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    LittleFsInPlaceModifier.Remove(archive, entryNames);
  }

  /// <summary>
  /// Resolves the writer's block size from the schema. "Auto"/absent keeps the
  /// writer's 4 KiB default; a pinned power-of-two size label is parsed back to bytes.
  /// </summary>
  private static uint ResolveBlockSize(FormatCreateOptions? options) {
    var parsed = FilesystemSchemaPresets.ParseSize(options?.GetOption("BlockSize", "Auto"));
    return parsed > 0 ? (uint)parsed : 4096u;
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(LittleFsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_offset={sb.SuperblockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_major={sb.VersionMajor}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_minor={sb.VersionMinor}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={sb.BlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_count={sb.BlockCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"total_blocks={sb.BlockCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"name_max={sb.NameMax}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"file_max={sb.FileMax}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"attr_max={sb.AttrMax}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"revision={sb.Revision}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>Reads just what the superblock surface needs.</summary>
  private static byte[] ReadHeader(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var want = stream.CanSeek ? (int)Math.Min(65536, stream.Length) : 65536;
    var buffer = new byte[want];
    var got = 0;
    while (got < want) {
      var n = stream.Read(buffer, got, want - got);
      if (n <= 0) break;
      got += n;
    }
    return got == want ? buffer : buffer[..got];
  }

  /// <summary>Copies the image itself out as FULL.littlefs, without buffering it.</summary>
  private static void WriteRawImage(string outputDir, Stream stream, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter("FULL.littlefs", filter)) return;
    if (!stream.CanSeek) return;
    var target = Path.Combine(outputDir, "FULL.littlefs");
    Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
    stream.Position = 0;
    using var output = File.Create(target);
    stream.CopyTo(output);
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Metadata pairs — the superblock pair and every directory's commit log —
  /// are structure; a file's CTZ blocks are its own. littlefs never erases a
  /// block it stops using, so what no live file or directory claims still
  /// holds whatever was written there last.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new LittleFsReader(image);
      var blockSize = (long)reader.BlockSize;
      if (blockSize <= 0) return [];

      foreach (var block in reader.MetadataBlocks) {
        var offset = block * blockSize;
        if (offset < 0 || offset >= image.Length) continue;
        result.Add(new DefragBlockInfo(offset, Math.Min(blockSize, image.Length - offset),
          DefragBlockKind.MetadataReserved));
      }

      foreach (var file in reader.Files)
        foreach (var block in reader.FileBlocks(file)) {
          var offset = block * blockSize;
          if (offset < 0 || offset >= image.Length) continue;
          result.Add(new DefragBlockInfo(offset, Math.Min(blockSize, image.Length - offset),
            DefragBlockKind.Used, file.Path));
        }

      if (result.Count == 0) return [];
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    // A CTZ block carries pointers as well as data, so its tail is not slack
    // that maps to the file's length — only whole free blocks are wiped.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
