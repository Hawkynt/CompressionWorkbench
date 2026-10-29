#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.DiskDoubler;

/// <summary>
/// Writes a single DiskDoubler file with a stored (method 0) data fork and no resource fork, in the
/// layout <see cref="DiskDoublerReader"/> documents. The Unarchiver (<c>unar</c>) reads it back.
/// </summary>
public sealed class DiskDoublerWriter {
  private byte[] _data = [];
  private string _fileType = "????";
  private string _creator = "????";

  /// <summary>Sets the data fork. DiskDoubler stores no name; the file's own name is the entry's name.</summary>
  public void SetFile(byte[] data, string fileType = "????", string creator = "????") {
    ArgumentNullException.ThrowIfNull(data);
    ArgumentNullException.ThrowIfNull(fileType);
    ArgumentNullException.ThrowIfNull(creator);
    this._data = data;
    this._fileType = fileType;
    this._creator = creator;
  }

  /// <summary>Writes the header and the stored data fork to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var hdr = new byte[DiskDoublerConstants.HeaderSize];
    BinaryPrimitives.WriteUInt32BigEndian(hdr, DiskDoublerConstants.Magic);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), (uint)this._data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(8), (uint)this._data.Length);
    // [12..20] resource fork sizes, [20..22] methods: zero = empty and stored.
    hdr[22] = 0x2A;  // the version byte real files carry
    WriteFourCc(hdr.AsSpan(32, 4), this._fileType);
    WriteFourCc(hdr.AsSpan(36, 4), this._creator);
    BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(82), Crc16Ccitt.Compute(hdr.AsSpan(0, DiskDoublerConstants.HeaderCrcSpan)));

    output.Write(hdr);
    output.Write(this._data);
  }

  private static void WriteFourCc(Span<byte> dest, string value) {
    var bytes = Encoding.Latin1.GetBytes(value);
    var length = Math.Min(bytes.Length, 4);
    bytes.AsSpan(0, length).CopyTo(dest);
    dest[length..].Fill((byte)' ');
  }
}
