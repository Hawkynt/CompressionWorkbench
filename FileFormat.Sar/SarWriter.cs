#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Sar;

/// <summary>
/// Writes NScripter SAR archives (uncompressed). Layout matches <see cref="SarReader"/>:
/// <list type="bullet">
///   <item>Header: uint16 BE file count, uint32 BE data offset.</item>
///   <item>Per entry: null-terminated ASCII filename, uint32 BE offset (relative to data start), uint32 BE size.</item>
///   <item>Data area: file data concatenated, in the same order as the index.</item>
/// </list>
/// </summary>
public sealed class SarWriter {
  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("Name must be non-empty.", nameof(name));
    if (name.Contains('\0'))
      throw new ArgumentException("Name must not contain null bytes.", nameof(name));
    _files.Add((name, data ?? throw new ArgumentNullException(nameof(data))));
  }

  public void WriteTo(Stream output) {
    if (_files.Count > ushort.MaxValue)
      throw new InvalidOperationException($"SAR supports at most {ushort.MaxValue} entries.");

    // Pass 1: compute index size to know data offset.
    long indexBytes = 0;
    foreach (var (name, _) in _files)
      indexBytes += Encoding.ASCII.GetByteCount(name) + 1 + 4 + 4; // name+null + offset + size

    var dataOffset = checked(2 + 4 + (uint)indexBytes); // header + index
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];

    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)_files.Count);
    output.Write(u16);
    BinaryPrimitives.WriteUInt32BigEndian(u32, dataOffset);
    output.Write(u32);

    // Index entries
    uint relOffset = 0;
    foreach (var (name, data) in _files) {
      var nameBytes = Encoding.ASCII.GetBytes(name);
      output.Write(nameBytes);
      output.WriteByte(0); // null terminator
      BinaryPrimitives.WriteUInt32BigEndian(u32, relOffset);
      output.Write(u32);
      BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)data.Length);
      output.Write(u32);
      relOffset = checked(relOffset + (uint)data.Length);
    }

    // Data area
    foreach (var (_, data) in _files)
      output.Write(data);
  }
}
