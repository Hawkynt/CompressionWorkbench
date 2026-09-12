#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.FreeArc;

/// <summary>
/// Builds minimal FreeArc archives (.arc) that can be read by <see cref="FreeArcReader"/>.
/// <para>
/// All files are stored without compression (method "storing"). The writer produces
/// the exact binary layout described in <see cref="FreeArcReader"/>: magic, archive flags,
/// a single directory block, a single data block, and the end-of-archive marker.
/// </para>
/// </summary>
public sealed class FreeArcWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the archive being built.</summary>
  /// <param name="name">The filename as it will appear in the archive directory.</param>
  /// <param name="data">The raw file data to store.</param>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Serialises the archive and returns it as a byte array.
  /// </summary>
  /// <returns>The complete FreeArc archive bytes.</returns>
  public byte[] Build() {
    using var ms = new MemoryStream();

    // Magic + archive flags (zero).
    ms.Write(FreeArcReader.Magic);
    ms.Write(stackalloc byte[4]); // flags = 0

    // Compute per-file data offsets within the concatenated payload.
    var offsets = new long[_files.Count];
    long dataPos = 0;
    for (var i = 0; i < _files.Count; i++) {
      offsets[i] = dataPos;
      dataPos   += _files[i].Data.Length;
    }

    // --- Directory block ---
    ms.WriteByte(0x01); // BlockTypeDir

    Span<byte> buf4 = stackalloc byte[4];
    Span<byte> buf2 = stackalloc byte[2];
    Span<byte> buf8 = stackalloc byte[8];

    BinaryPrimitives.WriteUInt32LittleEndian(buf4, (uint)_files.Count);
    ms.Write(buf4);

    for (var i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      var nameBytes   = Encoding.UTF8.GetBytes(name);
      var methodBytes = Encoding.ASCII.GetBytes("storing");

      BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)nameBytes.Length);
      ms.Write(buf2);
      ms.Write(nameBytes);

      BinaryPrimitives.WriteUInt64LittleEndian(buf8, (ulong)data.Length);
      ms.Write(buf8); // uncompressed size

      BinaryPrimitives.WriteUInt64LittleEndian(buf8, (ulong)data.Length);
      ms.Write(buf8); // compressed size (same — stored)

      BinaryPrimitives.WriteUInt64LittleEndian(buf8, (ulong)offsets[i]);
      ms.Write(buf8); // data offset

      BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)methodBytes.Length);
      ms.Write(buf2);
      ms.Write(methodBytes);
    }

    // --- Data block ---
    ms.WriteByte(0x02); // BlockTypeData

    BinaryPrimitives.WriteUInt32LittleEndian(buf4, (uint)dataPos);
    ms.Write(buf4);
    foreach (var (_, data) in _files)
      ms.Write(data);

    // --- End-of-archive marker ---
    ms.WriteByte(0x00);

    return ms.ToArray();
  }
}
