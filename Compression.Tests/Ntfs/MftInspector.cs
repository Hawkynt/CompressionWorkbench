using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Ntfs;

/// <summary>
/// Test-only reader that inspects raw NTFS MFT records: boot-sector geometry,
/// USA fixup undo, attribute walking and $FILE_NAME namespace extraction. Lets
/// the NTFS writer tests assert on the on-disk attribute layout (resident vs
/// non-resident $DATA, $FILE_NAME namespace bytes) without depending on the
/// reader's internal record state.
/// </summary>
internal static class MftInspector {

  private const int BytesPerSector = 512;

  // Reads the boot-sector geometry: (cluster size, MFT byte offset, record size).
  private static (int ClusterSize, long MftOffset, int RecordSize) Geometry(byte[] image) {
    var clusterSize = BytesPerSector * image[13];
    var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(48));
    var clustersPerRecord = (sbyte)image[64];
    var recordSize = clustersPerRecord < 0 ? 1 << (-clustersPerRecord) : clustersPerRecord * clusterSize;
    return (clusterSize, mftCluster * clusterSize, recordSize);
  }

  // Reads MFT record <paramref name="recordNumber"/> and undoes its USA fixup.
  internal static byte[] ReadRecord(byte[] image, uint recordNumber) {
    var (_, mftOffset, recordSize) = Geometry(image);
    var offset = (int)(mftOffset + recordNumber * recordSize);
    var record = image.AsSpan(offset, recordSize).ToArray();
    UndoUsaFixup(record);
    return record;
  }

  // Scans user MFT records (>= 16) for the one whose Win32/Win32&DOS $FILE_NAME
  // matches <paramref name="fileName"/>.
  internal static byte[] FindRecordByFileName(byte[] image, string fileName) {
    var (_, mftOffset, recordSize) = Geometry(image);
    for (uint rec = 16; ; rec++) {
      var off = (int)(mftOffset + rec * recordSize);
      if (off + recordSize > image.Length) break;
      if (image[off] != 'F' || image[off + 1] != 'I' || image[off + 2] != 'L' || image[off + 3] != 'E') break;

      var record = ReadRecord(image, rec);
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
      if ((flags & 0x01) == 0) continue; // not in use

      string? found = null;
      ForEachAttribute(record, (type, pos) => {
        if (type != 0x30) return;
        var (name, _) = ReadFileName(record, pos);
        found ??= name;
      });
      if (string.Equals(found, fileName, StringComparison.OrdinalIgnoreCase))
        return record;
    }
    throw new InvalidOperationException($"no MFT record carries $FILE_NAME '{fileName}'");
  }

  // Whether the record's unnamed default $DATA (type 0x80) is stored resident
  // (form code byte at +8 == 0) versus non-resident (1).
  internal static bool DataAttributeIsResident(byte[] record) {
    bool? resident = null;
    ForEachAttribute(record, (type, pos) => {
      if (type != 0x80) return;
      if (record[pos + 9] != 0) return; // named stream — skip
      resident ??= record[pos + 8] == 0;
    });
    return resident ?? throw new InvalidOperationException("record has no unnamed $DATA attribute");
  }

  // Whether the record's unnamed default $DATA attribute carries the NTFS
  // compressed flag (attribute-header flags at +12, bit 0x0001). Throws if the
  // record has no unnamed $DATA.
  internal static bool DataAttributeIsCompressed(byte[] record) {
    bool? compressed = null;
    ForEachAttribute(record, (type, pos) => {
      if (type != 0x80) return;
      if (record[pos + 9] != 0) return; // named stream — skip
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 12));
      compressed ??= (flags & 0x0001) != 0;
    });
    return compressed ?? throw new InvalidOperationException("record has no unnamed $DATA attribute");
  }

  // Counts the real (non-sparse) clusters allocated by the record's unnamed
  // non-resident $DATA attribute, by walking its data runs. Sparse runs
  // (offset-bytes nibble = 0) are skipped. Resident $DATA returns 0.
  internal static long DataRealClusterCount(byte[] record) {
    long total = 0;
    var seen = false;
    ForEachAttribute(record, (type, pos) => {
      if (type != 0x80 || record[pos + 9] != 0 || record[pos + 8] == 0) return; // non-resident unnamed only
      seen = true;
      var runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 32));
      var o = pos + runsOffset;
      long prev = 0;
      while (o < record.Length) {
        var header = record[o];
        if (header == 0) break;
        var lengthBytes = header & 0x0F;
        var offsetBytes = (header >> 4) & 0x0F;
        o++;
        long length = 0;
        for (var i = 0; i < lengthBytes; i++) length |= (long)record[o + i] << (i * 8);
        o += lengthBytes;
        if (offsetBytes > 0) {
          total += length; // real run
          o += offsetBytes;
        }
        // sparse run (offsetBytes == 0): no allocated clusters, skip
        _ = prev;
      }
    });
    if (!seen) return 0;
    return total;
  }

  // Reads the (major, minor) version from a $Volume record's $VOLUME_INFORMATION
  // (type 0x70) attribute value (offsets +8 and +9 of the resident value).
  internal static (byte Major, byte Minor) VolumeVersion(byte[] record) {
    (byte, byte)? version = null;
    ForEachAttribute(record, (type, pos) => {
      if (type != 0x70) return;
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 20));
      var v = pos + valueOffset;
      version ??= (record[v + 8], record[v + 9]);
    });
    return version ?? throw new InvalidOperationException("record has no $VOLUME_INFORMATION attribute");
  }

  // Every $FILE_NAME namespace byte in the record (offset +65 of each
  // attribute value): 0 POSIX, 1 Win32, 2 DOS, 3 Win32&DOS.
  internal static List<byte> FileNameNamespaces(byte[] record) {
    var namespaces = new List<byte>();
    ForEachAttribute(record, (type, pos) => {
      if (type != 0x30) return;
      var (_, ns) = ReadFileName(record, pos);
      namespaces.Add(ns);
    });
    return namespaces;
  }

  // All $FILE_NAME namespace bytes across every in-use user MFT record (>= 16).
  internal static List<byte> AllUserFileNameNamespaces(byte[] image) {
    var (_, mftOffset, recordSize) = Geometry(image);
    var result = new List<byte>();
    for (uint rec = 16; ; rec++) {
      var off = (int)(mftOffset + rec * recordSize);
      if (off + recordSize > image.Length) break;
      if (image[off] != 'F' || image[off + 1] != 'I' || image[off + 2] != 'L' || image[off + 3] != 'E') break;
      var record = ReadRecord(image, rec);
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
      if ((flags & 0x01) == 0) continue;
      result.AddRange(FileNameNamespaces(record));
    }
    return result;
  }

  private static (string Name, byte Namespace) ReadFileName(byte[] record, int attrPos) {
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 20));
    var dataStart = attrPos + valueOffset;
    var nameChars = record[dataStart + 64];
    var ns = record[dataStart + 65];
    var name = Encoding.Unicode.GetString(record, dataStart + 66, nameChars * 2);
    return (name, ns);
  }

  private static void ForEachAttribute(byte[] record, Action<uint, int> visit) {
    int pos = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    while (pos + 8 <= record.Length) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
      if (type == 0xFFFFFFFF) break;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
      if (len < 16 || pos + len > record.Length) break;
      visit(type, pos);
      pos += len;
    }
  }

  private static void UndoUsaFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * BytesPerSector - 2;
      if (sectorEnd + 2 > record.Length) break;
      record.AsSpan(usaOffset + i * 2, 2).CopyTo(record.AsSpan(sectorEnd));
    }
  }
}
