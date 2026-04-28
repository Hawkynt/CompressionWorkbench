#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.BcacheFs;

/// <summary>
/// Descriptor for BcacheFS volume images (modern Linux FS, mainlined in
/// kernel 6.7). Surfaces the parsed <c>bch_sb</c> superblock at offset 4096
/// as structured metadata plus the raw image, and emits a WORM-minimal,
/// SB-only image via <see cref="BcacheFsWriter"/> that <c>bcachefs show-super</c>
/// accepts. Walking the b-tree object graph (extents/dirents/inodes) and
/// emitting B-tree nodes are explicitly out of scope — see
/// <c>docs/FILESYSTEMS.md</c> for the full gap statement.
/// </summary>
public sealed class BcacheFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "BcacheFs";
  public string DisplayName => "BcacheFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".bcachefs";
  public IReadOnlyList<string> Extensions => [".bcachefs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 16-byte BcacheFS magic UUID at file offset 4120 (= 4096-byte pre-area +
    // 24 bytes into struct bch_sb to skip csum/version/version_min/pad).
    new(BcacheFsSuperblock.MagicUuid, Offset: BcacheFsSuperblock.MagicOffset, Confidence: 0.85f),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "BcacheFS Linux filesystem image — R/W (WORM, SB-validated only — fsck parity pending).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bcachefs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    BcacheFsSuperblock sb;
    try {
      sb = BcacheFsSuperblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bcachefs", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.bcachefs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength, sb.RawBytes.LongLength, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    BcacheFsSuperblock sb;
    try {
      sb = BcacheFsSuperblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.bcachefs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.bcachefs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
  }

  /// <summary>
  /// Emits a WORM-minimal BcacheFS image. The resulting image carries a
  /// spec-compliant <c>struct bch_sb</c> + four-copy <c>bch_sb_layout</c> +
  /// <c>BCH_SB_FIELD_members_v2</c> describing the single device. File
  /// content is NOT recoverable from the resulting image — there is no
  /// B-tree (extents / dirents / inodes are the multi-week follow-up).
  /// <c>bcachefs show-super</c> accepts the result; <c>bcachefs fsck</c>
  /// will still complain about the empty trees.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new BcacheFsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      // We surface the file list in metadata only — no content goes into the
      // image. Reading the file is therefore optional, but we honour it so
      // future scope expansion has the bytes available.
      try { w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath)); }
      catch { /* unreadable input — skip silently, image is SB-only anyway */ }
    }
    w.WriteTo(output);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(BcacheFsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"uuid={sb.Uuid}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"user_uuid={sb.UserUuid}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"label={sb.Label}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version={sb.FormatVersion()}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_raw={sb.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_min_raw={sb.VersionMin}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={sb.BlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"dev_idx={sb.DevIdx}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"nr_devices={sb.NrDevices}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"u64s={sb.U64s}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"offset={sb.Offset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"seq={sb.Seq}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  // Bounded read — only the superblock area (offset 4096 + ~1 KiB) is needed.
  // The 64 KiB cap keeps speculative carver scans from materialising multi-GB
  // candidate windows.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
