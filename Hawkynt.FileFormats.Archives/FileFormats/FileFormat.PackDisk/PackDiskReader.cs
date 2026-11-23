using System.Buffers.Binary;
using System.Text;
using FileFormat.Xpk;

namespace FileFormat.PackDisk;

/// <summary>
/// Reads an Amiga PackDisk archive (magic "DISKPACK", version 1) and rebuilds its floppy image.
/// </summary>
/// <remarks>
/// <para>Layout (big-endian): "DISKPACK", version (u16, 1), two pad bytes, the volume name (30 bytes,
/// NUL-padded), then 80 cylinder entries of 12 bytes — stored size (u16), file offset (u32), four pad
/// bytes, a flag byte (1 = the cylinder is stored as its 11,264 raw bytes) and a pad byte. Every other
/// cylinder is one XPK stream (<c>XPKF</c>) of the stored size that unpacks to 11,264 bytes.</para>
/// <para>XPK sub-libraries that Compression.Core does not decode, and encrypted streams, are refused.</para>
/// <para>Sourcing: layout from libxad's PackDisk client (The Unarchiver's XADMaster,
/// <c>libxad/clients/PackDisk.c</c>, LGPL-2.1-or-later); verified on archives PackDisk 1.1 wrote, which
/// rebuild their source floppy byte for byte.</para>
/// </remarks>
public sealed class PackDiskReader {
  /// <summary>Bytes in one cylinder: 2 tracks × 11 sectors × 512 bytes.</summary>
  public const int CylinderSize = 22 * 512;

  /// <summary>Cylinders on a DD floppy.</summary>
  public const int Cylinders = 80;

  private const int HeaderSize = 42;
  private const int EntrySize = 12;
  private const int DataStart = HeaderSize + Cylinders * EntrySize;

  private readonly byte[] _data;
  private readonly (int Size, int Offset, bool Raw)[] _entries = new (int, int, bool)[Cylinders];

  /// <summary>The volume name recorded in the header.</summary>
  public string VolumeName { get; }

  /// <summary>Returns true when <paramref name="data"/> starts with the PackDisk magic and version 1.</summary>
  public static bool IsPackDisk(ReadOnlySpan<byte> data) =>
    data.Length >= 10 && data[..8].SequenceEqual("DISKPACK"u8) && data[8] == 0 && data[9] == 1;

  /// <summary>Reads the header and the cylinder table.</summary>
  /// <exception cref="InvalidDataException">Not a PackDisk archive, or an entry points outside the file.</exception>
  public PackDiskReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    var d = this._data;
    if (!IsPackDisk(d))
      throw new InvalidDataException("PackDisk: missing DISKPACK header.");
    if (d.Length < DataStart)
      throw new InvalidDataException("PackDisk: truncated cylinder table.");
    this.VolumeName = Encoding.Latin1.GetString(d.AsSpan(12, 30)).Split('\0')[0];
    for (var c = 0; c < Cylinders; ++c) {
      var e = d.AsSpan(HeaderSize + c * EntrySize, EntrySize);
      var size = BinaryPrimitives.ReadUInt16BigEndian(e);
      var offset = BinaryPrimitives.ReadUInt32BigEndian(e[2..]);
      var raw = e[10] == 1;
      if (size == 0 || offset < DataStart || offset + size > (uint)d.Length || (raw && size != CylinderSize))
        throw new InvalidDataException($"PackDisk: cylinder {c} entry is out of range.");
      this._entries[c] = (size, (int)offset, raw);
    }
  }

  /// <summary>Names of the XPK sub-libraries the cylinders use ("RAW" for stored cylinders).</summary>
  public IReadOnlyList<string> Packers =>
    this._entries.Select(e => e.Raw ? "RAW" : XpkStream.ReadType(this._data.AsSpan(e.Offset, e.Size))).Distinct().ToList();

  /// <summary>Rebuilds the 880 KiB floppy.</summary>
  /// <exception cref="InvalidDataException">A stream is damaged or unpacks to the wrong size.</exception>
  /// <exception cref="NotSupportedException">An XPK sub-library is not implemented, or a stream is encrypted.</exception>
  public byte[] ExtractDisk() {
    var image = new byte[Cylinders * CylinderSize];
    for (var c = 0; c < Cylinders; ++c) {
      var (size, offset, raw) = this._entries[c];
      var cylinder = raw ? this._data.AsSpan(offset, size).ToArray() : XpkStream.Decode(this._data.AsSpan(offset, size).ToArray());
      if (cylinder.Length != CylinderSize)
        throw new InvalidDataException($"PackDisk: cylinder {c} unpacks to {cylinder.Length} bytes.");
      cylinder.CopyTo(image, c * CylinderSize);
    }
    return image;
  }
}
