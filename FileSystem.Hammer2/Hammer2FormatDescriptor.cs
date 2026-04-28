#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hammer2;

/// <summary>
/// Read-only descriptor for HAMMER2 (DragonFly BSD newer) filesystem images.
/// Surfaces the volume-data sector at offset 0 plus a structured metadata
/// bundle and the raw image. Walking the HAMMER2 cluster B-tree (radix-tree
/// chains, blockrefs, indirect blocks) is explicitly out of scope (multi-week
/// effort).
///
/// Magic: 8-byte uint64 at offset 0 = <c>HAMMER2_VOLUME_ID_HBO</c>
/// (<c>0x48414d3205172011</c>) or <c>HAMMER2_VOLUME_ID_ABO</c>
/// (<c>0x11201705324d4148</c>). The descriptor's <see cref="MagicSignatures"/>
/// list covers the HBO form (LE serialisation: <c>11 20 17 05 32 4D 41 48</c>);
/// the ABO form is recognised by the parser but is rare in practice (only
/// arises when a HAMMER2 image is cross-mounted on opposite-endian hardware).
/// Confidence 0.85: an 8-byte magic at offset 0 is high-confidence but the
/// detector does no secondary sanity check (e.g. volume size plausibility,
/// fstype UUID match).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer2/hammer2_disk.h</c></description></item>
///   <item><description><c>https://gitweb.dragonflybsd.org/dragonfly.git/blob/HEAD:/sys/vfs/hammer2/DESIGN</c></description></item>
/// </list>
/// </summary>
public sealed class Hammer2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Hammer2";
  public string DisplayName => "HAMMER2 (DragonFly BSD)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".hammer2";
  public IReadOnlyList<string> Extensions => [".hammer2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Hammer2VolumeData.MagicBytesHboLE, Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "HAMMER2 (DragonFly BSD newer) filesystem image — volume-data sector surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Hammer2VolumeData hdr;
    try {
      hdr = Hammer2VolumeData.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer2", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.hammer2", image.LongLength, image.LongLength, "stored", false, false, null));
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

    Hammer2VolumeData hdr;
    try {
      hdr = Hammer2VolumeData.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.hammer2", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.hammer2", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr), files);
    if (hdr.Valid)
      WriteIfMatch(outputDir, "volume_header.bin", hdr.HeaderRaw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Hammer2VolumeData h) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(h.Valid ? "ok" : "partial")}\n");
    b.Append(ic, $"magic=0x{h.Magic:X16}\n");
    if (h.Valid) {
      b.Append(ic, $"byte_swapped={h.ByteSwapped}\n");
      b.Append(ic, $"version={h.Version}\n");
      b.Append(ic, $"flags=0x{h.Flags:X8}\n");
      b.Append(ic, $"volu_size={h.VoluSize}\n");
      b.Append(ic, $"copyid={h.CopyId}\n");
      b.Append(ic, $"freemap_version={h.FreemapVersion}\n");
      b.Append(ic, $"peer_type={h.PeerType}\n");
      b.Append(ic, $"volu_id={h.VoluId}\n");
      b.Append(ic, $"nvolumes={h.NVolumes}\n");
      b.Append(ic, $"boot_beg=0x{h.BootBeg:X16}\n");
      b.Append(ic, $"boot_end=0x{h.BootEnd:X16}\n");
      b.Append(ic, $"aux_beg=0x{h.AuxBeg:X16}\n");
      b.Append(ic, $"aux_end=0x{h.AuxEnd:X16}\n");
      b.Append(ic, $"fs_uuid_hex={h.FsidHex}\n");
      b.Append(ic, $"fs_type_uuid_hex={h.FsTypeHex}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. The HAMMER2 sector #0 lives in the first 512 bytes;
  // 64 KB is exactly one VOLUME_BYTES sector and provides comfortable headroom.
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
