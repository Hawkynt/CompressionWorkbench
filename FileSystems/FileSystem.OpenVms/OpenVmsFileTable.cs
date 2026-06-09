#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.OpenVms;

/// <summary>
/// Parses the CWB-OVMS-WB file table that <see cref="OpenVmsWriter"/> emits at
/// LBN 2 (byte offset 1024) of an ODS-2 volume. Real OpenVMS images never carry
/// this extension; our writer always does, so the reader can round-trip the
/// caller's files without us having to walk a real INDEXF.SYS + 000000.DIR.
/// </summary>
public sealed class OpenVmsFileTable {
  /// <summary>16-byte CWB-OVMS-WB eyecatcher placed at the start of LBN 2.</summary>
  public static readonly byte[] Eyecatcher = [
    (byte)'O', (byte)'V', (byte)'M', (byte)'S', (byte)'W', (byte)'B', (byte)'F', (byte)'T',
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
  ];

  internal const long FileTableBlockOffset = 1024;

  public sealed class Entry {
    public string Name { get; init; } = "";
    public long Offset { get; init; }
    public long Size { get; init; }
  }

  public List<Entry> Entries { get; } = new();

  /// <summary>
  /// Best-effort parse. Returns an empty table when the eyecatcher is absent
  /// (the image is a real OpenVMS volume or a writer that didn't add files).
  /// </summary>
  public static OpenVmsFileTable TryParse(ReadOnlySpan<byte> image) {
    var ft = new OpenVmsFileTable();
    if (image.Length < FileTableBlockOffset + Eyecatcher.Length + 4) return ft;
    var ecSpan = image.Slice((int)FileTableBlockOffset, Eyecatcher.Length);
    if (!ecSpan.SequenceEqual(Eyecatcher.AsSpan())) return ft;
    var cursor = (int)FileTableBlockOffset + Eyecatcher.Length;
    var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(cursor, 4));
    cursor += 4;
    if (fileCount > 4096) return ft;             // sanity cap

    for (var i = 0; i < fileCount; i++) {
      if (image.Length < cursor + 18) return ft;
      var offset = BinaryPrimitives.ReadInt64LittleEndian(image.Slice(cursor, 8)); cursor += 8;
      var length = BinaryPrimitives.ReadInt64LittleEndian(image.Slice(cursor, 8)); cursor += 8;
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(cursor, 2)); cursor += 2;
      if (image.Length < cursor + nameLen) return ft;
      var name = Encoding.UTF8.GetString(image.Slice(cursor, nameLen));
      cursor += nameLen;
      if (offset < 0 || length < 0 || offset + length > image.Length) return ft;
      ft.Entries.Add(new Entry { Name = name, Offset = offset, Size = length });
    }
    return ft;
  }

  /// <summary>Returns the payload bytes for an entry.</summary>
  public byte[] Extract(ReadOnlySpan<byte> image, Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset < 0 || entry.Size <= 0) return [];
    if (entry.Offset + entry.Size > image.Length) return [];
    var buf = new byte[entry.Size];
    image.Slice((int)entry.Offset, (int)entry.Size).CopyTo(buf);
    return buf;
  }
}
