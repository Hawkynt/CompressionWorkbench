#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dmg;

/// <summary>
/// Writes Apple Disk Image (DMG) files in WORM mode. Each input file becomes
/// one partition with a single raw (uncompressed) mish block. The output
/// roundtrips through <see cref="DmgReader"/>:
/// <list type="bullet">
///   <item>Layout: [partition data sectors] [XML plist] [512-byte koly trailer].</item>
///   <item>Each partition has a mish table with one <c>BlockTypeRaw</c> entry covering all its sectors plus a terminator.</item>
///   <item>No compression -- DMG's zlib/bz2/lzfse encoders aren't paired here, and raw is fully spec-valid.</item>
///   <item>No checksums -- mish/koly checksum-type fields set to 0 ("none"), which the reader accepts.</item>
/// </list>
/// </summary>
public sealed class DmgWriter {
  private const int SectorSize = 512;
  private const int KolySize = 512;
  private const int MishHeaderSize = 204;
  private const int MishBlockSize = 40;
  private const uint BlockTypeRaw = 0x00000001;
  private const uint BlockTypeTerminator = 0xFFFFFFFF;
  private static readonly byte[] KolyMagic = "koly"u8.ToArray();
  private static readonly byte[] MishMagic = "mish"u8.ToArray();

  private readonly List<(string name, byte[] data)> _partitions = [];

  public void AddPartition(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("Name must be non-empty.", nameof(name));
    _partitions.Add((name, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Pad each partition to a sector boundary so sectorCount is exact.
    var padded = new (string name, byte[] data)[_partitions.Count];
    for (var i = 0; i < _partitions.Count; i++) {
      var (name, data) = _partitions[i];
      var paddedLen = ((data.Length + SectorSize - 1) / SectorSize) * SectorSize;
      var buf = new byte[paddedLen];
      data.CopyTo(buf, 0);
      padded[i] = (name, buf);
    }

    // Compute layout: partition data sequentially from offset 0.
    var partitionOffsets = new long[padded.Length];
    long pos = 0;
    for (var i = 0; i < padded.Length; i++) {
      partitionOffsets[i] = pos;
      pos += padded[i].data.Length;
    }
    var dataForkLength = pos;

    // Build mish blob per partition (raw block + terminator).
    var mishBlobs = new byte[padded.Length][];
    for (var i = 0; i < padded.Length; i++) {
      var sectorCount = (ulong)(padded[i].data.Length / SectorSize);
      mishBlobs[i] = BuildMishBlob(
        firstSector: 0,
        sectorCount: sectorCount,
        rawDataOffset: (ulong)partitionOffsets[i],
        rawDataLength: (ulong)padded[i].data.Length);
    }

    // Build XML plist after data fork.
    var xml = BuildXmlPlist(padded, mishBlobs);
    var xmlBytes = Encoding.UTF8.GetBytes(xml);
    var xmlOffset = pos;
    pos += xmlBytes.Length;

    // ---- Write data fork ----
    foreach (var (_, data) in padded)
      output.Write(data);

    // ---- Write XML plist ----
    output.Write(xmlBytes);

    // ---- Write koly trailer ----
    Span<byte> koly = stackalloc byte[KolySize];
    koly.Clear();
    KolyMagic.CopyTo(koly);
    BinaryPrimitives.WriteUInt32BigEndian(koly[4..], 4);              // version
    BinaryPrimitives.WriteUInt32BigEndian(koly[8..], KolySize);       // header size
    BinaryPrimitives.WriteUInt32BigEndian(koly[12..], 1);             // flags
    BinaryPrimitives.WriteUInt64BigEndian(koly[16..], 0);             // running data fork offset
    BinaryPrimitives.WriteUInt64BigEndian(koly[24..], 0);             // data fork offset (always 0 for unsegmented)
    BinaryPrimitives.WriteUInt64BigEndian(koly[32..], (ulong)dataForkLength);
    BinaryPrimitives.WriteUInt64BigEndian(koly[40..], 0);             // resource fork offset
    BinaryPrimitives.WriteUInt64BigEndian(koly[48..], 0);             // resource fork length
    BinaryPrimitives.WriteUInt32BigEndian(koly[56..], 1);             // segment number
    BinaryPrimitives.WriteUInt32BigEndian(koly[60..], 1);             // segment count
    // Segment GUID (16 bytes at 64): leave zero
    BinaryPrimitives.WriteUInt32BigEndian(koly[80..], 0);             // data checksum type = none
    BinaryPrimitives.WriteUInt32BigEndian(koly[84..], 0);             // data checksum size = 0
    // 128 bytes data checksum at 88: zeros
    BinaryPrimitives.WriteUInt64BigEndian(koly[216..], (ulong)xmlOffset);
    BinaryPrimitives.WriteUInt64BigEndian(koly[224..], (ulong)xmlBytes.Length);
    // 120 bytes reserved at 232: zeros
    BinaryPrimitives.WriteUInt32BigEndian(koly[352..], 0);            // master checksum type = none
    BinaryPrimitives.WriteUInt32BigEndian(koly[356..], 0);            // master checksum size = 0
    // 128 bytes master checksum at 360: zeros
    BinaryPrimitives.WriteUInt32BigEndian(koly[488..], 1);            // image variant
    var totalSectors = (ulong)(dataForkLength / SectorSize);
    BinaryPrimitives.WriteUInt64BigEndian(koly[492..], totalSectors); // sector count
    output.Write(koly);
  }

  // ── Mish blob ─────────────────────────────────────────────────────────────

  private static byte[] BuildMishBlob(ulong firstSector, ulong sectorCount,
      ulong rawDataOffset, ulong rawDataLength) {
    // Two block entries: one raw covering all sectors, plus terminator.
    var blob = new byte[MishHeaderSize + 2 * MishBlockSize];

    MishMagic.CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), 1);                       // version
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(8), firstSector);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(16), sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(24), 0);                      // dataStart (unused by reader)
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(32), (uint)SectorSize);       // decompressedBufferRequested
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(36), 0);                      // blocksDescriptor
    // 24 reserved + 4 checksumType + 4 checksumSize + 128 checksum data = zeros
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(200), 2);                     // numBlockEntries

    // Block 0: raw
    var off = MishHeaderSize;
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(off + 0), BlockTypeRaw);
    // 4 reserved bytes
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 8), 0);                 // sectorOffset (within partition)
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 16), sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 24), rawDataOffset);    // absolute file offset
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 32), rawDataLength);

    // Block 1: terminator
    off += MishBlockSize;
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(off + 0), BlockTypeTerminator);

    return blob;
  }

  // ── XML plist ─────────────────────────────────────────────────────────────

  private static string BuildXmlPlist((string name, byte[] data)[] partitions, byte[][] mishBlobs) {
    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
    sb.AppendLine("<plist version=\"1.0\">");
    sb.AppendLine("<dict>");
    sb.AppendLine("  <key>resource-fork</key>");
    sb.AppendLine("  <dict>");
    sb.AppendLine("    <key>blkx</key>");
    sb.AppendLine("    <array>");
    for (var i = 0; i < partitions.Length; i++) {
      sb.AppendLine("      <dict>");
      sb.Append("        <key>Name</key><string>").Append(EscapeXml(partitions[i].name)).AppendLine("</string>");
      sb.Append("        <key>Data</key><data>").Append(Convert.ToBase64String(mishBlobs[i])).AppendLine("</data>");
      sb.AppendLine("      </dict>");
    }
    sb.AppendLine("    </array>");
    sb.AppendLine("  </dict>");
    sb.AppendLine("</dict>");
    sb.Append("</plist>");
    return sb.ToString();
  }

  private static string EscapeXml(string s) {
    return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
  }
}
