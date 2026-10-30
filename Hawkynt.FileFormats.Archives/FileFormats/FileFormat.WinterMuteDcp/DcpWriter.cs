using System.Buffers.Binary;
using Compression.Core.Deflate;
using FileFormat.Zlib;

namespace FileFormat.WinterMuteDcp;

/// <summary>A file to write: its decoded length and its bytes as they go into the package.</summary>
public sealed record DcpWriteEntry(string Name, uint Length, byte[] Stored, bool IsCompressed, uint Flags = 0, uint TimeDate1 = 0, uint TimeDate2 = 0) {

  /// <summary>Builds an entry from content, zlib-compressing it when that is smaller, as ProjectMan does.</summary>
  public static DcpWriteEntry FromData(string name, byte[] data, bool compress, uint timeDate = 0) {
    ArgumentNullException.ThrowIfNull(data);
    if (compress && data.Length > 0) {
      var packed = ZlibStream.Compress(data, DeflateCompressionLevel.Default);
      if (packed.Length < data.Length)
        return new DcpWriteEntry(name, (uint)data.Length, packed, true, 0, timeDate);
    }
    return new DcpWriteEntry(name, (uint)data.Length, data, false, 0, timeDate);
  }
}

/// <summary>Header fields of a package to write.</summary>
public sealed record DcpPackageInfo(string Name = "data", uint GameVersion = 1, byte Priority = 0, byte Cd = 0, uint CreationTime = 0, string Description = "", byte Pad = 0);

/// <summary>
/// Writes a version 0x200 WinterMute package the way WME 1.x's ProjectMan builds one: header,
/// directory-offset field, file data in directory order, then the single directory. The directory's
/// name must be the package's file name without <c>.dcp</c>: the engine opens <c>&lt;name&gt;.dcp</c>
/// to read the data. <see cref="DcpReader"/> documents the layout.
/// </summary>
public static class DcpWriter {

  /// <summary>Writes the package.</summary>
  public static void Write(Stream output, DcpPackageInfo info, IReadOnlyList<DcpWriteEntry> entries) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(info);
    ArgumentNullException.ThrowIfNull(entries);
    var packageName = Encode(info.Name, nameof(info));
    var description = DcpReader.NameEncoding.GetBytes(info.Description);
    if (description.Length > 99)
      throw new ArgumentException("WinterMute package description is limited to 99 bytes.", nameof(info));
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var encodedNames = new List<byte[]>(entries.Count);
    foreach (var e in entries) {
      if (!names.Add(e.Name))
        throw new ArgumentException($"Duplicate WinterMute file name '{e.Name}' (the engine ignores case).", nameof(entries));
      encodedNames.Add(Encode(e.Name.Replace('/', '\\'), nameof(entries)));
    }

    using var image = new MemoryStream();
    var header = new byte[DcpReader.HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, DcpReader.Magic1);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), DcpReader.Magic2);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), DcpReader.Version2);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), info.GameVersion);
    header[16] = info.Priority;
    header[17] = info.Cd;
    header[18] = 0; // not a master index
    header[19] = info.Pad;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), info.CreationTime);
    description.CopyTo(header, 24);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(124), 1);
    image.Write(header);
    WriteUInt32(image, 0); // directory offset, patched below

    var offsets = new uint[entries.Count];
    for (var i = 0; i < entries.Count; ++i) {
      offsets[i] = checked((uint)image.Position);
      image.Write(entries[i].Stored);
    }

    var dirOffset = checked((uint)image.Position);
    WriteName(image, packageName, xor: false);
    image.WriteByte(info.Cd);
    WriteUInt32(image, (uint)entries.Count);
    for (var i = 0; i < entries.Count; ++i) {
      var e = entries[i];
      WriteName(image, encodedNames[i], xor: true);
      WriteUInt32(image, offsets[i]);
      WriteUInt32(image, e.Length);
      WriteUInt32(image, e.IsCompressed ? checked((uint)e.Stored.Length) : 0);
      WriteUInt32(image, e.Flags);
      WriteUInt32(image, e.TimeDate1);
      WriteUInt32(image, e.TimeDate2);
    }

    var bytes = image.GetBuffer().AsSpan(0, (int)image.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes[DcpReader.HeaderSize..], dirOffset);
    output.Write(bytes);
  }

  private static byte[] Encode(string name, string param) {
    if (string.IsNullOrEmpty(name) || name.Contains('\0'))
      throw new ArgumentException($"Invalid WinterMute name '{name}'.", param);
    var bytes = DcpReader.NameEncoding.GetBytes(name);
    if (bytes.Length > 254 || DcpReader.NameEncoding.GetString(bytes) != name)
      throw new ArgumentException($"WinterMute name '{name}' does not fit a single-byte, 254-character name.", param);
    return bytes;
  }

  private static void WriteName(Stream s, byte[] name, bool xor) {
    s.WriteByte((byte)(name.Length + 1));
    foreach (var b in name)
      s.WriteByte(xor ? (byte)(b ^ DcpReader.NameKey) : b);
    s.WriteByte(xor ? DcpReader.NameKey : (byte)0);
  }

  private static void WriteUInt32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    s.Write(b);
  }
}
