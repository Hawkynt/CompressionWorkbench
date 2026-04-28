#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Reiser4;

/// <summary>
/// Read-only descriptor for Reiser4 filesystem images (successor to ReiserFS 3.6
/// — completely different on-disk layout). Surfaces the master superblock at
/// offset 65536 and, when present, the format40 superblock that follows it,
/// plus a structured metadata bundle and the raw image. Walking the twig-level
/// B-tree is explicitly out of scope (multi-week effort).
///
/// Magic:
/// <list type="bullet">
///   <item><description><c>"ReIsEr4"</c> at offset 65536 — master superblock <c>ms_magic[16]</c>.</description></item>
/// </list>
/// </summary>
public sealed class Reiser4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {
  public string Id => "Reiser4";
  public string DisplayName => "Reiser4";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate;
  public string DefaultExtension => ".reiser4";
  public IReadOnlyList<string> Extensions => [".reiser4"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "ReIsEr4" at byte offset 65536 (= 16 * 4096). Confidence 0.9: the 7-byte
    // magic is highly unlikely to land at exactly this offset by chance, but
    // it shares the "ReIsEr" prefix with the older ReiserFS 3.6 magic
    // ("ReIsErFs", "ReIsEr2Fs", "ReIsEr3Fs") which lives at offset 65536+52.
    // The disambiguation is unambiguous (different offsets, different suffixes)
    // so 0.9 is appropriate — slightly below the 0.95 used for unique 4+ byte
    // magics that live at position 0.
    new("ReIsEr4"u8.ToArray(), Offset: (int)Reiser4MasterSb.MasterOffset, Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Reiser4 filesystem image — master + format40 superblock surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      // Stream blew up before we got anywhere. Surface the irreducible minimum.
      entries.Add(new ArchiveEntryInfo(0, "FULL.reiser4", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Reiser4MasterSb sb;
    try {
      sb = Reiser4MasterSb.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.reiser4", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.reiser4", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "master_superblock.bin", sb.MasterRaw.LongLength, sb.MasterRaw.LongLength, "stored", false, false, null));
    if (sb.Format40Present)
      entries.Add(new ArchiveEntryInfo(idx++, "format40_superblock.bin", sb.Format40Raw.LongLength, sb.Format40Raw.LongLength, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    Reiser4MasterSb sb;
    try {
      sb = Reiser4MasterSb.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.reiser4", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.reiser4", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "master_superblock.bin", sb.MasterRaw, files);
    if (sb.Format40Present)
      WriteIfMatch(outputDir, "format40_superblock.bin", sb.Format40Raw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Reiser4MasterSb sb) {
    var b = new StringBuilder();
    b.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    b.Append(CultureInfo.InvariantCulture, $"master_offset={Reiser4MasterSb.MasterOffset}\n");
    b.Append(CultureInfo.InvariantCulture, $"blocksize={sb.BlockSize}\n");
    b.Append(CultureInfo.InvariantCulture, $"disk_plugin_id={sb.DiskPluginId}\n");
    b.Append(CultureInfo.InvariantCulture, $"uuid_hex={sb.UuidHex}\n");
    b.Append(CultureInfo.InvariantCulture, $"label={sb.Label}\n");
    b.Append(CultureInfo.InvariantCulture, $"format40_present={sb.Format40Present}\n");
    if (sb.Format40Present) {
      b.Append(CultureInfo.InvariantCulture, $"block_count={sb.BlockCount}\n");
      b.Append(CultureInfo.InvariantCulture, $"free_blocks={sb.FreeBlocks}\n");
      b.Append(CultureInfo.InvariantCulture, $"root_block={sb.RootBlock}\n");
      b.Append(CultureInfo.InvariantCulture, $"file_count={sb.FileCount}\n");
      b.Append(CultureInfo.InvariantCulture, $"mkfs_id=0x{sb.MkfsId:X8}\n");
      b.Append(CultureInfo.InvariantCulture, $"tree_height={sb.TreeHeight}\n");
      b.Append(CultureInfo.InvariantCulture, $"tail_policy={sb.Policy}\n");
      b.Append(CultureInfo.InvariantCulture, $"format40_version={sb.Format40Version}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // ── IArchiveCreatable ────────────────────────────────────────────────
  // Reiser4 is a filesystem image — there's no "list of files to add at the
  // root" concept that maps cleanly to an archive's content model. Our
  // creator emits an empty filesystem with just "/" (root dir + . + ..).
  // For files-on-image we'd need to grow the storage tree, which is out of
  // scope for the WORM-minimal writer. Inputs are silently ignored after
  // a labeled metadata note in the format options.
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var w = new Reiser4Writer();
    // We surface the encryption-password slot as the volume label since
    // FormatCreateOptions doesn't carry filesystem labels natively. No
    // encryption is implemented.
    if (!string.IsNullOrEmpty(options?.Password))
      w.Label = options.Password;
    w.Write(output);
  }

  // ── IArchiveWriteConstraints ─────────────────────────────────────────
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    // We accept nothing — the writer creates a strictly empty filesystem.
    reason = "Reiser4 writer emits an empty filesystem only (root directory + . + ..). " +
             "Files-on-image require multi-week storage-tree grow logic that is out of scope.";
    return false;
  }
  public long? MaxTotalArchiveSize => 0;
  public long? MinTotalArchiveSize => Reiser4Writer.BlockSize * (long)Reiser4Writer.MinBlockCount; // 16 MB
  public string AcceptedInputsDescription =>
    "Reiser4 writer emits an empty filesystem; inputs are not stored.";

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. Master SB is at 65536, format40 SB at 65536+blocksize
  // (4 KB typical). Cap at 96 KB so the format40 block at 69632..70112 always
  // fits even when blocksize is 4 KB.
  private const int HeaderReadCap = 96 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
