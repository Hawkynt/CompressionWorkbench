#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dmg;

/// <summary>
/// Writes Apple Disk Image (DMG/UDIF) files using raw <c>mish</c> blocks.
/// Each input becomes one partition and the exact caller-visible byte length is
/// carried in a private plist key so non-sector-aligned inputs round-trip without
/// exposing the mandatory 512-byte UDIF sector padding.
/// </summary>
public sealed class DmgWriter {
  internal const int SectorSize = 512;
  internal const int KolySize = 512;
  internal const int MishHeaderSize = 204;
  internal const int MishBlockSize = 40;
  internal const uint BlockTypeRaw = 0x00000001;
  internal const uint BlockTypeTerminator = 0xFFFFFFFF;
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

    var padded = new (string name, byte[] data, long logicalSize)[_partitions.Count];
    for (var i = 0; i < _partitions.Count; i++) {
      var (name, data) = _partitions[i];
      var paddedLen = AlignSector(data.Length);
      var buf = new byte[paddedLen];
      data.CopyTo(buf, 0);
      padded[i] = (name, buf, data.LongLength);
    }

    var partitionOffsets = new long[padded.Length];
    long pos = 0;
    for (var i = 0; i < padded.Length; i++) {
      partitionOffsets[i] = pos;
      pos += padded[i].data.Length;
    }
    var dataForkLength = pos;

    var mishBlobs = new byte[padded.Length][];
    for (var i = 0; i < padded.Length; i++) {
      var sectorCount = (ulong)(padded[i].data.Length / SectorSize);
      mishBlobs[i] = BuildMishBlob(
        firstSector: 0,
        sectorCount: sectorCount,
        rawDataOffset: (ulong)partitionOffsets[i],
        rawDataLength: (ulong)padded[i].data.Length);
    }

    var xml = BuildXmlPlist(padded, mishBlobs);
    var xmlBytes = Encoding.UTF8.GetBytes(xml);
    var xmlOffset = pos;

    foreach (var (_, data, _) in padded)
      output.Write(data);
    output.Write(xmlBytes);

    var totalSectors = padded.Aggregate<(string name, byte[] data, long logicalSize), ulong>(0,
      (current, partition) => current + (ulong)(partition.data.Length / SectorSize));
    output.Write(BuildKoly(xmlOffset, xmlBytes.LongLength, dataForkLength, totalSectors));
  }

  internal static int AlignSector(int length) {
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    return checked((length + SectorSize - 1) / SectorSize * SectorSize);
  }

  internal static byte[] BuildMishBlob(ulong firstSector, ulong sectorCount,
      ulong rawDataOffset, ulong rawDataLength) {
    var blob = new byte[MishHeaderSize + 2 * MishBlockSize];

    MishMagic.CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(8), firstSector);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(16), sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(24), 0);
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(32), SectorSize);
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(36), 0);
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(200), 2);

    var off = MishHeaderSize;
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(off), BlockTypeRaw);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 8), 0);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 16), sectorCount);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 24), rawDataOffset);
    BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(off + 32), rawDataLength);

    off += MishBlockSize;
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(off), BlockTypeTerminator);
    return blob;
  }

  internal static string BuildBlkxDict(string name, byte[] mish, long logicalSize) {
    var sb = new StringBuilder();
    sb.AppendLine("      <dict>");
    sb.Append("        <key>Name</key><string>").Append(EscapeXml(name)).AppendLine("</string>");
    sb.Append("        <key>CWBLogicalSize</key><integer>").Append(logicalSize).AppendLine("</integer>");
    sb.Append("        <key>Data</key><data>").Append(Convert.ToBase64String(mish)).AppendLine("</data>");
    sb.Append("      </dict>");
    return sb.ToString();
  }

  internal static byte[] BuildKoly(long xmlOffset, long xmlLength, long dataForkLength, ulong totalSectors,
      byte[]? template = null) {
    if (xmlOffset < 0 || xmlLength < 0 || dataForkLength < 0)
      throw new ArgumentOutOfRangeException(nameof(xmlOffset));

    var koly = template is { Length: KolySize } ? (byte[])template.Clone() : new byte[KolySize];
    KolyMagic.CopyTo(koly, 0);
    BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(4), 4);
    BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(8), KolySize);
    if (template == null) {
      BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(12), 1);
      BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(16), 0);
      BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(24), 0);
      BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(56), 1);
      BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(60), 1);
      BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(488), 1);
    }

    BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(32), (ulong)dataForkLength);
    BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(216), (ulong)xmlOffset);
    BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(224), (ulong)xmlLength);
    BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(492), totalSectors);

    // The plist and/or data fork changed. A checksum of type "none" is valid
    // UDIF and avoids retaining a checksum that now describes stale bytes.
    koly.AsSpan(80, 136).Clear();
    koly.AsSpan(352, 136).Clear();
    return koly;
  }

  private static string BuildXmlPlist((string name, byte[] data, long logicalSize)[] partitions, byte[][] mishBlobs) {
    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
    sb.AppendLine("<plist version=\"1.0\">");
    sb.AppendLine("<dict>");
    sb.AppendLine("  <key>resource-fork</key>");
    sb.AppendLine("  <dict>");
    sb.AppendLine("    <key>blkx</key>");
    sb.AppendLine("    <array>");
    for (var i = 0; i < partitions.Length; i++)
      sb.AppendLine(BuildBlkxDict(partitions[i].name, mishBlobs[i], partitions[i].logicalSize));
    sb.AppendLine("    </array>");
    sb.AppendLine("  </dict>");
    sb.AppendLine("</dict>");
    sb.Append("</plist>");
    return sb.ToString();
  }

  internal static string EscapeXml(string s) {
    return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
  }
}
