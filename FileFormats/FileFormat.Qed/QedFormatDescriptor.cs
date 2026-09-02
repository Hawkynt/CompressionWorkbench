#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Qed;

/// <summary>
/// QED (QEMU Enhanced Disk) image. Header (little-endian) after the 4-byte magic
/// <c>QED\0</c> (0x00444551): cluster_size (u32), table_size (u32, clusters),
/// header_size (u32, clusters), features (u64), compat_features (u64),
/// autoclear_features (u64), l1_table_offset (u64), image_size (u64),
/// backing_filename_offset (u32), backing_filename_size (u32).
///
/// <para>The disk is mapped by a two-level table: the L1 table holds offsets to
/// L2 tables, each L2 table holds offsets to data clusters. Unallocated entries
/// read back as zero. This descriptor surfaces <c>FULL.qed</c> verbatim, a
/// <c>metadata.ini</c> (virtual size, cluster/table geometry, backing file) and —
/// when the L1/L2 geometry is sane — a fully reconstructed <c>disk.raw</c> built
/// by walking L1 → L2 → cluster. Read-only; malformed headers degrade to
/// FULL + partial metadata without throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://wiki.qemu.org/Features/QED</c> — QEMU wiki — QED feature page and specification</description></item>
///   <item><description><c>docs/interop/qed_spec.rst</c> in the QEMU source tree — on-disk layout</description></item>
/// </list>
/// </summary>
public sealed class QedFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Qed";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "QEMU Enhanced Disk (QED)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".qed";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".qed"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x51, 0x45, 0x44, 0x00], Confidence: 0.95), // "QED\0"
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
    "QEMU Enhanced Disk (QED): two-level L1/L2 cluster-mapped sparse disk image; reconstructs disk.raw read-only.";

  private const uint QedMagic = 0x00444551; // 'Q','E','D',0 little-endian

  private sealed record QedHeader(
    uint ClusterSize,
    uint TableSize,
    uint HeaderSize,
    ulong Features,
    ulong L1TableOffset,
    ulong ImageSize,
    uint BackingOffset,
    uint BackingSize,
    string? BackingFile,
    bool Valid);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var header = TryReadHeader(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.qed", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };
    if (header.Valid && header.BackingFile == null && header.ImageSize > 0 && header.ImageSize <= int.MaxValue)
      entries.Add(new ArchiveEntryInfo(2, "disk.raw", (long)header.ImageSize, (long)header.ImageSize, "Stored", false, false, null));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.qed"))
      WriteFile(outputDir, "FULL.qed", ReadAll(stream));

    var header = TryReadHeader(stream);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(header)));

    if (Wants(files, "disk.raw") && header.Valid && header.BackingFile == null &&
        header.ImageSize > 0 && header.ImageSize <= int.MaxValue) {
      var disk = TryReconstruct(stream, header);
      if (disk != null)
        WriteFile(outputDir, "disk.raw", disk);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static QedHeader TryReadHeader(Stream stream) {
    try {
      if (!stream.CanSeek) return Invalid();
      stream.Position = 0;
      Span<byte> h = stackalloc byte[68];
      if (!TryReadExact(stream, h)) return Invalid();
      var magic = BinaryPrimitives.ReadUInt32LittleEndian(h[..4]);
      if (magic != QedMagic) return Invalid();

      var clusterSize = BinaryPrimitives.ReadUInt32LittleEndian(h[4..8]);
      var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(h[8..12]);
      var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(h[12..16]);
      var features = BinaryPrimitives.ReadUInt64LittleEndian(h[16..24]);
      // compat_features h[24..32], autoclear_features h[32..40]
      var l1Offset = BinaryPrimitives.ReadUInt64LittleEndian(h[40..48]);
      var imageSize = BinaryPrimitives.ReadUInt64LittleEndian(h[48..56]);
      var backingOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[56..60]);
      var backingSize = BinaryPrimitives.ReadUInt32LittleEndian(h[60..64]);

      string? backing = null;
      if (backingSize is > 0 and < 4096 && backingOffset > 0 &&
          backingOffset + backingSize <= stream.Length) {
        stream.Position = backingOffset;
        var nameBuf = new byte[backingSize];
        if (TryReadExact(stream, nameBuf))
          backing = Encoding.UTF8.GetString(nameBuf);
      }

      return new QedHeader(clusterSize, tableSize, headerSize, features, l1Offset,
        imageSize, backingOffset, backingSize, backing, Valid: true);
    } catch {
      return Invalid();
    }
  }

  private static QedHeader Invalid()
    => new(0, 0, 0, 0, 0, 0, 0, 0, null, Valid: false);

  // Walk L1 -> L2 -> data clusters and materialize the full raw disk.
  // Returns null when geometry is implausible (degrade to FULL + metadata only).
  private static byte[]? TryReconstruct(Stream stream, QedHeader h) {
    try {
      var clusterSize = h.ClusterSize;
      if (clusterSize == 0 || (clusterSize & (clusterSize - 1)) != 0) return null;
      if (clusterSize is < 4096 or > (64 * 1024 * 1024)) return null;
      // features bit 0 = backing file, bit 1 = need check, bit 2 = backing format no-probe.
      // Unknown incompatible features beyond these we cannot honor.
      if ((h.Features & ~0x7UL) != 0) return null;

      var tableBytes = (long)h.TableSize * clusterSize;
      if (tableBytes <= 0 || tableBytes > 64 * 1024 * 1024) return null;
      var entriesPerTable = tableBytes / 8;
      if (entriesPerTable <= 0) return null;

      var clusterBits = BitOperations_TrailingZeroCount(clusterSize);
      var l2Bits = BitOperations_Log2((ulong)entriesPerTable);
      if ((1L << l2Bits) != entriesPerTable) return null; // must be power of two

      var imageSize = (long)h.ImageSize;
      if (imageSize <= 0 || imageSize > int.MaxValue) return null;
      var disk = new byte[imageSize];

      var l1 = ReadTable(stream, (long)h.L1TableOffset, entriesPerTable);
      if (l1 == null) return null;

      var clusterMask = clusterSize - 1;
      var l2Mask = (ulong)(entriesPerTable - 1);

      for (long pos = 0; pos < imageSize; pos += clusterSize) {
        var clusterIndex = (ulong)pos >> clusterBits;
        var l1Index = (long)(clusterIndex >> l2Bits);
        var withinL2 = (long)(clusterIndex & l2Mask);
        if (l1Index >= l1.Length) break;
        var l2Offset = l1[l1Index] & ~(ulong)clusterMask;
        if (l2Offset == 0) continue; // unallocated -> zero
        var l2 = ReadTable(stream, (long)l2Offset, entriesPerTable);
        if (l2 == null) continue;
        if (withinL2 >= l2.Length) continue;
        var dataOffset = l2[withinL2] & ~(ulong)clusterMask;
        if (dataOffset == 0) continue; // unallocated -> zero (and zero/one sentinels)
        if ((long)dataOffset + clusterSize > stream.Length) continue;
        stream.Position = (long)dataOffset;
        var toCopy = (int)Math.Min(clusterSize, imageSize - pos);
        var slab = new byte[toCopy];
        if (!TryReadExact(stream, slab)) continue;
        Array.Copy(slab, 0, disk, pos, toCopy);
      }
      return disk;
    } catch {
      return null;
    }
  }

  private static ulong[]? ReadTable(Stream stream, long offset, long entryCount) {
    if (offset <= 0 || entryCount <= 0) return null;
    var byteLen = entryCount * 8;
    if (offset + byteLen > stream.Length) return null;
    stream.Position = offset;
    var buf = new byte[byteLen];
    if (!TryReadExact(stream, buf)) return null;
    var table = new ulong[entryCount];
    for (long i = 0; i < entryCount; ++i)
      table[i] = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan((int)(i * 8), 8));
    return table;
  }

  private static int BitOperations_TrailingZeroCount(uint v) {
    var n = 0;
    while ((v & 1) == 0 && v != 0) { v >>= 1; ++n; }
    return n;
  }

  private static int BitOperations_Log2(ulong v) {
    var n = 0;
    while (v > 1) { v >>= 1; ++n; }
    return n;
  }

  private static string BuildMetadataIni(QedHeader h) {
    var sb = new StringBuilder();
    sb.Append("[Qed]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(h.Valid ? 1 : 0)}\n");
    if (!h.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"cluster_size={h.ClusterSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"table_size_clusters={h.TableSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_size_clusters={h.HeaderSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"features=0x{h.Features:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"l1_table_offset={h.L1TableOffset}\n");
    sb.Append(CultureInfo.InvariantCulture, $"image_size={h.ImageSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_backing_file={(h.BackingFile != null ? 1 : 0)}\n");
    if (h.BackingFile != null)
      sb.Append(CultureInfo.InvariantCulture, $"backing_file={h.BackingFile.Replace('\n', ' ').Replace('\r', ' ')}\n");
    sb.Append("parse_status=ok\n");
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
