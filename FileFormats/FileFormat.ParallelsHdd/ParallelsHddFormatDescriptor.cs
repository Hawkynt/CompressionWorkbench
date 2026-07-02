#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ParallelsHdd;

/// <summary>
/// Parallels expanding disk image (<c>.hds</c> data file inside a <c>.hdd</c> bundle).
/// The header (little-endian) is: 16-byte magic (<c>"WithoutFreeSpace"</c> for the
/// v1 layout, <c>"WithouFreSpaExt"</c> / <c>"WithFreeSpace"</c> variants exist),
/// u32 version, u32 heads, u32 cylinders, u32 sectors-per-track (tracks block size,
/// in sectors), u32 image-size (in sectors), u32 BAT entry count, then the BAT — an
/// array of u32 sector offsets, one per block. A BAT entry of 0 means the block is
/// unallocated and reads back as zero; otherwise the entry is the start sector of the
/// block's data.
///
/// <para>This descriptor surfaces a verbatim <c>FULL.hds</c>, a <c>metadata.ini</c>
/// (geometry, block size, in-use block count) and — when the BAT geometry is sane —
/// a fully reconstructed <c>disk.raw</c> built by walking the BAT block by block.
/// Read-only; malformed headers degrade to FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/qemu/qemu/blob/master/docs/interop/parallels.txt</c> — QEMU's independent documentation of the Parallels expanding-disk layout</description></item>
///   <item><description><c>https://www.qemu.org</c> — QEMU — maintained implementation (parallels block driver)</description></item>
/// </list>
/// </summary>
public sealed class ParallelsHddFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "ParallelsHdd";
  public string DisplayName => "Parallels Disk (HDS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".hds";
  public IReadOnlyList<string> Extensions => [".hds"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("WithoutFreeSpace"u8.ToArray(), Confidence: 0.9),
    new("WithFreeSpace\0\0\0"u8.ToArray(), Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Parallels expanding disk (.hds): WithoutFreeSpace/WithFreeSpace header + geometry + BAT of " +
    "u32 sector offsets. Surfaces FULL.hds, metadata.ini and a BAT-reconstructed disk.raw. Read-only.";

  private const int SectorSize = 512;

  private sealed record HdsHeader(
    bool Valid,
    string Magic,
    uint Version,
    uint Heads,
    uint Cylinders,
    uint BlockSizeSectors,
    uint ImageSizeSectors,
    uint BatEntries,
    long BatOffset);

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var h = TryReadHeader(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.hds", fullSize, fullSize, "Stored", false, false, null, Kind: "Track"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
    };
    if (h.Valid && IsReconstructable(h, stream))
      entries.Add(new ArchiveEntryInfo(2, "disk.raw", DiskBytes(h), DiskBytes(h), "Stored", false, false, null, Kind: "Track"));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.hds"))
      WriteFile(outputDir, "FULL.hds", ReadAll(stream));

    var h = TryReadHeader(stream);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(h, stream)));

    if (Wants(files, "disk.raw") && h.Valid && IsReconstructable(h, stream)) {
      var disk = TryReconstruct(stream, h);
      if (disk != null)
        WriteFile(outputDir, "disk.raw", disk);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static long DiskBytes(HdsHeader h) => (long)h.ImageSizeSectors * SectorSize;

  private static bool IsReconstructable(HdsHeader h, Stream stream) {
    if (h.BlockSizeSectors == 0 || h.BatEntries == 0) return false;
    var diskBytes = DiskBytes(h);
    if (diskBytes <= 0 || diskBytes > int.MaxValue) return false;
    if ((long)h.BatEntries * 4 > 256L * 1024 * 1024) return false;
    return h.BatOffset + (long)h.BatEntries * 4 <= SafeLength(stream);
  }

  private static HdsHeader TryReadHeader(Stream stream) {
    try {
      if (!stream.CanSeek || stream.Length < 64) return Invalid();
      stream.Position = 0;
      Span<byte> h = stackalloc byte[64];
      if (!TryReadExact(stream, h)) return Invalid();

      var magicBytes = h[..16];
      var magic = Encoding.ASCII.GetString(magicBytes).TrimEnd('\0');
      var known = magic.StartsWith("WithoutFreeSpace", StringComparison.Ordinal) ||
                  magic.StartsWith("WithouFreSpaExt", StringComparison.Ordinal) ||
                  magic.StartsWith("WithFreeSpace", StringComparison.Ordinal);
      if (!known) return Invalid();

      var version = BinaryPrimitives.ReadUInt32LittleEndian(h[16..20]);
      var heads = BinaryPrimitives.ReadUInt32LittleEndian(h[20..24]);
      var cylinders = BinaryPrimitives.ReadUInt32LittleEndian(h[24..28]);
      var blockSizeSectors = BinaryPrimitives.ReadUInt32LittleEndian(h[28..32]);
      var imageSizeSectors = BinaryPrimitives.ReadUInt32LittleEndian(h[32..36]);
      var batEntries = BinaryPrimitives.ReadUInt32LittleEndian(h[36..40]);
      // The BAT begins immediately after the 64-byte header.
      const long batOffset = 64;

      return new HdsHeader(true, magic, version, heads, cylinders, blockSizeSectors,
        imageSizeSectors, batEntries, batOffset);
    } catch {
      return Invalid();
    }
  }

  private static HdsHeader Invalid()
    => new(false, string.Empty, 0, 0, 0, 0, 0, 0, 0);

  // Walk the BAT and materialize the full raw disk. Each BAT entry is a start sector;
  // 0 means the block is unallocated (reads back zero). Returns null when geometry is
  // implausible (degrade to FULL + metadata only).
  private static byte[]? TryReconstruct(Stream stream, HdsHeader h) {
    try {
      var diskBytes = DiskBytes(h);
      if (diskBytes <= 0 || diskBytes > int.MaxValue) return null;
      var blockBytes = (long)h.BlockSizeSectors * SectorSize;
      if (blockBytes <= 0 || blockBytes > 256L * 1024 * 1024) return null;

      var bat = ReadBat(stream, h);
      if (bat == null) return null;

      var disk = new byte[diskBytes];
      for (var blockIndex = 0; blockIndex < bat.Length; ++blockIndex) {
        var startSector = bat[blockIndex];
        if (startSector == 0) continue; // unallocated -> zero
        var srcOffset = (long)startSector * SectorSize;
        var destOffset = blockIndex * blockBytes;
        if (destOffset >= diskBytes) break;
        if (srcOffset + blockBytes > stream.Length) continue;
        var toCopy = (int)Math.Min(blockBytes, diskBytes - destOffset);
        stream.Position = srcOffset;
        var slab = new byte[toCopy];
        if (!TryReadExact(stream, slab)) continue;
        Array.Copy(slab, 0, disk, destOffset, toCopy);
      }
      return disk;
    } catch {
      return null;
    }
  }

  private static uint[]? ReadBat(Stream stream, HdsHeader h) {
    var byteLen = (long)h.BatEntries * 4;
    if (h.BatOffset + byteLen > stream.Length) return null;
    stream.Position = h.BatOffset;
    var buf = new byte[byteLen];
    if (!TryReadExact(stream, buf)) return null;
    var bat = new uint[h.BatEntries];
    for (var i = 0; i < bat.Length; ++i)
      bat[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4, 4));
    return bat;
  }

  private static string BuildMetadataIni(HdsHeader h, Stream stream) {
    var sb = new StringBuilder();
    sb.Append("[ParallelsHdd]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(h.Valid ? 1 : 0)}\n");
    if (!h.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"magic={h.Magic}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={h.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"heads={h.Heads}\n");
    sb.Append(CultureInfo.InvariantCulture, $"cylinders={h.Cylinders}\n");
    sb.Append(CultureInfo.InvariantCulture, $"block_size_sectors={h.BlockSizeSectors}\n");
    sb.Append(CultureInfo.InvariantCulture, $"image_size_sectors={h.ImageSizeSectors}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bat_entries={h.BatEntries}\n");
    sb.Append(CultureInfo.InvariantCulture, $"disk_size_bytes={DiskBytes(h)}\n");

    var inUse = 0;
    if (IsReconstructable(h, stream)) {
      var bat = ReadBat(stream, h);
      if (bat != null)
        foreach (var e in bat)
          if (e != 0) ++inUse;
    }
    sb.Append(CultureInfo.InvariantCulture, $"blocks_in_use={inUse}\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(IsReconstructable(h, stream) ? "ok" : "partial")}\n");
    return sb.ToString();
  }

  private static bool TryReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
