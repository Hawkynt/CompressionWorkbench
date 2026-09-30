using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Zip;

/// <summary>
/// One central directory record exactly as it is stored: the 46-byte fixed part, the name, the
/// extra field and the comment, byte for byte. Only what an edit has to change is ever patched —
/// the name, its length, the local header offset — so everything else an archive carries survives:
/// the host that made it (and so the meaning of its external attributes), every flag, the internal
/// attributes, every extra field, the comment in whatever encoding it was written.
/// </summary>
internal sealed class ZipRawRecord {
  private const int FixedLength = 46;

  public required byte[] Fixed { get; init; }
  public required byte[] NameBytes { get; set; }
  public required byte[] Extra { get; set; }
  public required byte[] Comment { get; init; }

  public ushort Flags {
    get => BinaryPrimitives.ReadUInt16LittleEndian(this.Fixed.AsSpan(8));
    set => BinaryPrimitives.WriteUInt16LittleEndian(this.Fixed.AsSpan(8), value);
  }

  public bool IsUtf8 => (this.Flags & ZipConstants.FlagUtf8) != 0;

  /// <summary>The name, decoded the way the reader decodes it.</summary>
  public string Name => (this.IsUtf8 ? Encoding.UTF8 : Encoding.Latin1).GetString(this.NameBytes);

  private uint Compressed32 => BinaryPrimitives.ReadUInt32LittleEndian(this.Fixed.AsSpan(20));
  private uint Uncompressed32 => BinaryPrimitives.ReadUInt32LittleEndian(this.Fixed.AsSpan(24));
  private uint Offset32 => BinaryPrimitives.ReadUInt32LittleEndian(this.Fixed.AsSpan(42));

  /// <summary>Whether either size lives in the ZIP64 extra field, which also makes a data descriptor's sizes 8 bytes wide.</summary>
  public bool HasZip64Sizes => this.Compressed32 == ZipConstants.Zip64Sentinel32 || this.Uncompressed32 == ZipConstants.Zip64Sentinel32;

  public long CompressedSize => this.Compressed32 == ZipConstants.Zip64Sentinel32 ? this.Zip64Field(1) : this.Compressed32;

  public long LocalHeaderOffset {
    get => this.Offset32 == ZipConstants.Zip64Sentinel32 ? this.Zip64Field(2) : this.Offset32;
    set {
      if (this.Offset32 == ZipConstants.Zip64Sentinel32) {
        this.SetZip64Field(2, value);
        return;
      }

      if (value > uint.MaxValue)
        throw new NotSupportedException("The edit would move an entry past 4 GiB, which needs a ZIP64 record this entry does not have.");
      BinaryPrimitives.WriteUInt32LittleEndian(this.Fixed.AsSpan(42), (uint)value);
    }
  }

  /// <summary>Replaces the name, switching the record — and so its local header — to UTF-8 when the new name needs it.</summary>
  public void Rename(string name) {
    var needsUtf8 = name.Any(c => c > 0x7F);
    if (needsUtf8 && !this.IsUtf8) this.Flags |= ZipConstants.FlagUtf8;
    this.NameBytes = (this.IsUtf8 ? Encoding.UTF8 : Encoding.Latin1).GetBytes(name);
    if (this.NameBytes.Length > ushort.MaxValue) throw new InvalidDataException("ZIP file name exceeds the 65535-byte header limit.");
  }

  public void WriteTo(Stream output) {
    BinaryPrimitives.WriteUInt16LittleEndian(this.Fixed.AsSpan(28), (ushort)this.NameBytes.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(this.Fixed.AsSpan(30), (ushort)this.Extra.Length);
    output.Write(this.Fixed);
    output.Write(this.NameBytes);
    output.Write(this.Extra);
    output.Write(this.Comment);
  }

  public static ZipRawRecord Read(Stream input) {
    var fixedPart = ReadExactly(input, FixedLength);
    if (BinaryPrimitives.ReadUInt32LittleEndian(fixedPart) != ZipConstants.CentralDirectorySignature)
      throw new InvalidDataException("Invalid central directory signature.");

    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(28));
    var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(30));
    var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(32));
    return new() {
      Fixed = fixedPart,
      NameBytes = ReadExactly(input, nameLength),
      Extra = ReadExactly(input, extraLength),
      Comment = ReadExactly(input, commentLength),
    };
  }

  // The ZIP64 extra field carries, in this order and only for the 32-bit fields holding the
  // sentinel: uncompressed size, compressed size, local header offset. Index 0/1/2 names them.
  private long Zip64Field(int index) {
    var at = this.Zip64FieldPosition(index);
    return at < 0 ? throw new InvalidDataException("ZIP64 field is missing.") : BinaryPrimitives.ReadInt64LittleEndian(this.Extra.AsSpan(at));
  }

  private void SetZip64Field(int index, long value) {
    var at = this.Zip64FieldPosition(index);
    if (at < 0) throw new InvalidDataException("ZIP64 field is missing.");
    BinaryPrimitives.WriteInt64LittleEndian(this.Extra.AsSpan(at), value);
  }

  private int Zip64FieldPosition(int index) {
    for (var pos = 0; pos + 4 <= this.Extra.Length;) {
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(this.Extra.AsSpan(pos));
      var size = BinaryPrimitives.ReadUInt16LittleEndian(this.Extra.AsSpan(pos + 2));
      var body = pos + 4;
      if (tag == ZipConstants.Zip64ExtraFieldTag) {
        var at = body;
        uint[] fields = [this.Uncompressed32, this.Compressed32, this.Offset32];
        for (var i = 0; i < 3; ++i) {
          if (fields[i] != ZipConstants.Zip64Sentinel32) continue;
          if (i == index) return at + 8 <= body + size ? at : -1;
          at += 8;
        }

        return -1;
      }

      pos = body + size;
    }

    return -1;
  }

  internal static byte[] ReadExactly(Stream input, int count) {
    var buffer = new byte[count];
    input.ReadExactly(buffer);
    return buffer;
  }
}

/// <summary>
/// The central directory of a ZIP archive as raw records, and the edits that can be made to an
/// archive without decoding a single entry: renaming entries by copying every local header and
/// its data verbatim, and rewriting the directory after an entry was added or dropped.
/// </summary>
internal static class ZipRawDirectory {
  private const int LocalHeaderFixedLength = 30;

  /// <summary>The directory's records, and the archive comment as stored.</summary>
  public static (List<ZipRawRecord> Records, long DirectoryOffset, byte[] Comment) Read(Stream zip) {
    var (offset, _, count, _) = ZipEndOfCentralDirectory.Read(zip);
    var eocd = ZipEndOfCentralDirectory.FindEocd(zip);
    zip.Position = eocd + 20;
    var commentLength = (int)(ReadUInt16(zip));
    var comment = ZipRawRecord.ReadExactly(zip, commentLength);

    zip.Position = offset;
    var records = new List<ZipRawRecord>(count);
    for (var i = 0; i < count; ++i)
      records.Add(ZipRawRecord.Read(zip));

    return (records, offset, comment);
  }

  /// <summary>
  /// Writes the directory and its end records at the stream's position: raw records as they are,
  /// plus any new entries serialised in full.
  /// </summary>
  public static void Write(Stream zip, IReadOnlyList<ZipRawRecord> records, IReadOnlyList<ZipEntry> added, byte[] comment) {
    var directoryOffset = zip.Position;
    foreach (var record in records)
      record.WriteTo(zip);

    using (var writer = new BinaryWriter(zip, Encoding.Latin1, leaveOpen: true))
      foreach (var entry in added)
        ZipCentralDirectoryEntry.Write(writer, entry);

    WriteEnd(zip, directoryOffset, zip.Position - directoryOffset, records.Count + added.Count, comment);
  }

  /// <summary>
  /// Copies <paramref name="source"/> to <paramref name="destination"/> with entries renamed by
  /// <paramref name="rename"/> (null keeps a name). Every local header keeps its fields and extra
  /// field; the compressed data and any data descriptor are copied byte for byte, so nothing is
  /// decoded, recompressed or re-encrypted.
  /// </summary>
  public static void CopyRenamed(Stream source, Stream destination, Func<string, string?> rename) {
    var (records, _, comment) = Read(source);
    var buffer = new byte[1 << 16];

    foreach (var record in records) {
      var newName = rename(record.Name);
      source.Position = record.LocalHeaderOffset;
      var header = ZipRawRecord.ReadExactly(source, LocalHeaderFixedLength);
      if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ZipConstants.LocalFileHeaderSignature)
        throw new InvalidDataException($"No local header where the directory places \"{record.Name}\".");

      var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
      var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
      var localName = ZipRawRecord.ReadExactly(source, nameLength);
      var localExtra = ZipRawRecord.ReadExactly(source, extraLength);

      if (newName is not null && newName != record.Name) {
        record.Rename(newName);
        localName = record.NameBytes;
        var localFlags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (record.IsUtf8) BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)(localFlags | ZipConstants.FlagUtf8));
      }

      var dataLength = record.CompressedSize + DescriptorLength(source, record, source.Position + record.CompressedSize);
      record.LocalHeaderOffset = destination.Position;

      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)localName.Length);
      destination.Write(header);
      destination.Write(localName);
      destination.Write(localExtra);
      CopyBytes(source, destination, dataLength, buffer);
    }

    Write(destination, records, [], comment);
  }

  /// <summary>The length of the data descriptor after an entry's data, or 0 when it has none.</summary>
  private static long DescriptorLength(Stream source, ZipRawRecord record, long at) {
    if ((record.Flags & ZipConstants.FlagDataDescriptor) == 0) return 0;

    var sizes = record.HasZip64Sizes ? 16 : 8;
    if (at + 4 > source.Length) return 0;
    var keep = source.Position;
    source.Position = at;
    var signed = ReadUInt32(source) == ZipConstants.DataDescriptorSignature;
    source.Position = keep;
    return (signed ? 4 : 0) + 4 + sizes;
  }

  private static void CopyBytes(Stream source, Stream destination, long count, byte[] buffer) {
    while (count > 0) {
      var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
      if (read <= 0) throw new EndOfStreamException("The archive ends inside an entry's data.");
      destination.Write(buffer, 0, read);
      count -= read;
    }
  }

  private static void WriteEnd(Stream zip, long directoryOffset, long directorySize, int count, byte[] comment) {
    using var writer = new BinaryWriter(zip, Encoding.Latin1, leaveOpen: true);
    var zip64 = directoryOffset > uint.MaxValue || directorySize > uint.MaxValue || count > ushort.MaxValue;
    if (zip64) {
      var zip64Offset = zip.Position;
      writer.Write(ZipConstants.Zip64EndOfCentralDirectorySignature);
      writer.Write(44L);
      writer.Write(ZipConstants.VersionMadeBy20);
      writer.Write(ZipConstants.VersionNeeded45);
      writer.Write(0u);
      writer.Write(0u);
      writer.Write((long)count);
      writer.Write((long)count);
      writer.Write(directorySize);
      writer.Write(directoryOffset);
      writer.Write(ZipConstants.Zip64EndOfCentralDirectoryLocatorSignature);
      writer.Write(0u);
      writer.Write(zip64Offset);
      writer.Write(1u);
    }

    writer.Write(ZipConstants.EndOfCentralDirectorySignature);
    writer.Write((ushort)0);
    writer.Write((ushort)0);
    writer.Write(zip64 ? ZipConstants.Zip64Sentinel16 : (ushort)count);
    writer.Write(zip64 ? ZipConstants.Zip64Sentinel16 : (ushort)count);
    writer.Write(zip64 ? ZipConstants.Zip64Sentinel32 : (uint)directorySize);
    writer.Write(zip64 ? ZipConstants.Zip64Sentinel32 : (uint)directoryOffset);
    writer.Write((ushort)comment.Length);
    writer.Write(comment);
  }

  private static ushort ReadUInt16(Stream s) => BinaryPrimitives.ReadUInt16LittleEndian(ZipRawRecord.ReadExactly(s, 2));
  private static uint ReadUInt32(Stream s) => BinaryPrimitives.ReadUInt32LittleEndian(ZipRawRecord.ReadExactly(s, 4));
}
