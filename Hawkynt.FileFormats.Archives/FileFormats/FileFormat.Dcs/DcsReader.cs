using System.Buffers.Binary;
using FileFormat.Xpk;

namespace FileFormat.Dcs;

/// <summary>
/// Reads an Amiga DCS (Device Compression System) archive and rebuilds its floppy image.
/// </summary>
/// <remarks>
/// <para>Layout (big-endian): the six-byte header <c>DCS</c>, two zero bytes and the number of
/// encryption passes; then one record per cylinder, in ascending order without gaps: <c>TR</c>, a zero
/// byte, the cylinder number (u8), the stream length (u16) and one XPK stream (<c>XPKF</c>) of that
/// length that unpacks to the 11,264 bytes of the cylinder. Cylinders outside the recorded range read
/// as zeros. See <c>docs/DCS-ON-DISK.md</c>.</para>
/// <para>Encrypted archives (a non-zero pass count) and XPK sub-libraries that Compression.Core does
/// not decode are refused.</para>
/// <para>Sourcing: layout from libxad's DCS client (The Unarchiver's XADMaster,
/// <c>libxad/clients/DCS.c</c>, LGPL-2.1-or-later); verified on archives DCS 0.10 wrote, which rebuild
/// their source floppy byte for byte.</para>
/// </remarks>
public sealed class DcsReader {
  /// <summary>Bytes in one cylinder: 2 tracks × 11 sectors × 512 bytes.</summary>
  public const int CylinderSize = 22 * 512;

  /// <summary>Cylinders on a DD floppy.</summary>
  public const int Cylinders = 80;

  /// <summary>Bytes in the file header.</summary>
  public const int HeaderSize = 6;

  /// <summary>Bytes in a cylinder record header.</summary>
  public const int RecordHeaderSize = 6;

  private readonly byte[] _data;
  private readonly List<(int Cylinder, int Offset, int Size)> _records = [];

  /// <summary>Encryption passes recorded in the header (0 = not encrypted).</summary>
  public int EncryptionPasses { get; }

  /// <summary>First cylinder in the archive.</summary>
  public int LowCylinder => this._records[0].Cylinder;

  /// <summary>Last cylinder in the archive.</summary>
  public int HighCylinder => this._records[^1].Cylinder;

  /// <summary>Returns true when <paramref name="data"/> starts like a DCS archive.</summary>
  public static bool IsDcs(ReadOnlySpan<byte> data) =>
    data.Length >= 8 && data[..5].SequenceEqual("DCS\0\0"u8) && data[6] == (byte)'T' && data[7] == (byte)'R';

  /// <summary>Reads the header and walks the cylinder records.</summary>
  /// <exception cref="InvalidDataException">Not a DCS archive, or a record is damaged or out of order.</exception>
  public DcsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    var d = this._data;
    if (!IsDcs(d))
      throw new InvalidDataException("DCS: missing DCS header.");
    this.EncryptionPasses = d[5];
    var pos = HeaderSize;
    while (pos < d.Length) {
      if (pos + RecordHeaderSize > d.Length)
        throw new InvalidDataException($"DCS: truncated record header at offset {pos}.");
      if (d[pos] != 'T' || d[pos + 1] != 'R' || d[pos + 2] != 0)
        throw new InvalidDataException($"DCS: bad record marker at offset {pos}.");
      int cylinder = d[pos + 3];
      int size = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 4));
      if (cylinder >= Cylinders || (this._records.Count > 0 && cylinder != this._records[^1].Cylinder + 1))
        throw new InvalidDataException($"DCS: cylinder {cylinder} out of order at offset {pos}.");
      if (pos + RecordHeaderSize + size > d.Length)
        throw new InvalidDataException($"DCS: cylinder {cylinder} runs past the end of the file.");
      this._records.Add((cylinder, pos + RecordHeaderSize, size));
      pos += RecordHeaderSize + size;
    }
    if (this._records.Count == 0)
      throw new InvalidDataException("DCS: archive holds no cylinders.");
  }

  /// <summary>Names of the XPK sub-libraries the cylinders use.</summary>
  public IReadOnlyList<string> Packers =>
    this._records.Select(r => XpkStream.ReadType(this._data.AsSpan(r.Offset, r.Size))).Distinct().ToList();

  /// <summary>Rebuilds the 880 KiB floppy; cylinders outside the archive are zero.</summary>
  /// <exception cref="InvalidDataException">A stream is damaged or unpacks to the wrong size.</exception>
  /// <exception cref="NotSupportedException">The archive is encrypted, or an XPK sub-library is not implemented.</exception>
  public byte[] ExtractDisk() {
    if (this.EncryptionPasses != 0)
      throw new NotSupportedException($"DCS: archive is encrypted ({this.EncryptionPasses} pass(es)).");
    var image = new byte[Cylinders * CylinderSize];
    foreach (var (cylinder, offset, size) in this._records) {
      var unpacked = XpkStream.Decode(this._data.AsSpan(offset, size).ToArray());
      if (unpacked.Length != CylinderSize)
        throw new InvalidDataException($"DCS: cylinder {cylinder} unpacks to {unpacked.Length} bytes.");
      unpacked.CopyTo(image, cylinder * CylinderSize);
    }
    return image;
  }
}
