#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hammer;

/// <summary>
/// Read-only descriptor for HAMMER (DragonFly BSD original) filesystem images.
/// Surfaces the volume header at offset 0 plus a structured metadata bundle and
/// the raw image. Walking the HAMMER B-tree (zone blockmap → cluster → inode →
/// records) is explicitly out of scope (multi-week effort).
///
/// Magic: 8-byte uint64 <c>vol_signature = 0xC8414D4DC5523031</c> ("HAMMER01")
/// at offset 0, serialised LE on disk as <c>31 30 52 C5 4D 4D 41 C8</c>.
/// Confidence 0.85: an 8-byte magic value at offset 0 is high-confidence but
/// HAMMER lacks an additional sanity check at this stage of detection
/// (the <c>vol_fstype</c> UUID at offset 64 is not validated against a
/// well-known constant).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer/hammer_disk.h</c></description></item>
///   <item><description><c>https://www.dragonflybsd.org/hammer/</c></description></item>
/// </list>
/// </summary>
public sealed class HammerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Hammer";
  public string DisplayName => "HAMMER (DragonFly BSD)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".hammer";
  public IReadOnlyList<string> Extensions => [".hammer"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(HammerVolumeOndisk.MagicBytesLE, Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "HAMMER (DragonFly BSD original) filesystem image — volume header surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      // Stream blew up before we got anywhere — irreducible minimum.
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    HammerVolumeOndisk hdr;
    try {
      hdr = HammerVolumeOndisk.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.hammer", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (hdr.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "volume_header.bin", hdr.HeaderRaw.LongLength, hdr.HeaderRaw.LongLength, "stored", false, false, null));
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

    HammerVolumeOndisk hdr;
    try {
      hdr = HammerVolumeOndisk.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.hammer", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.hammer", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr), files);
    if (hdr.Valid)
      WriteIfMatch(outputDir, "volume_header.bin", hdr.HeaderRaw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(HammerVolumeOndisk h) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(h.Valid ? "ok" : "partial")}\n");
    b.Append(ic, $"vol_signature=0x{h.VolSignature:X16}\n");
    if (h.Valid) {
      b.Append(ic, $"vol_label={h.VolLabel}\n");
      b.Append(ic, $"vol_no={h.VolNo}\n");
      b.Append(ic, $"vol_count={h.VolCount}\n");
      b.Append(ic, $"vol_version={h.VolVersion}\n");
      b.Append(ic, $"vol_flags=0x{h.VolFlags:X8}\n");
      b.Append(ic, $"vol_rootvol={h.VolRootVol}\n");
      b.Append(ic, $"vol_crc=0x{h.VolCrc:X8}\n");
      b.Append(ic, $"fs_uuid_hex={h.VolFsidHex}\n");
      b.Append(ic, $"fs_type_uuid_hex={h.VolFsTypeHex}\n");
      b.Append(ic, $"vol_bot_beg=0x{h.VolBotBeg:X16}\n");
      b.Append(ic, $"vol_mem_beg=0x{h.VolMemBeg:X16}\n");
      b.Append(ic, $"vol_buf_beg=0x{h.VolBufBeg:X16}\n");
      b.Append(ic, $"vol_buf_end=0x{h.VolBufEnd:X16}\n");
      b.Append(ic, $"vol0_btree_root=0x{h.Vol0BtreeRoot:X16}\n");
      b.Append(ic, $"vol0_next_tid=0x{h.Vol0NextTid:X16}\n");
      b.Append(ic, $"vol0_stat_bigblocks={h.Vol0StatBigblocks}\n");
      b.Append(ic, $"vol0_stat_freebigblocks={h.Vol0StatFreeBigblocks}\n");
      b.Append(ic, $"vol0_stat_inodes={h.Vol0StatInodes}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. The HAMMER volume header lives entirely in the first
  // ~2 KB; 64 KB is comfortable headroom.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
