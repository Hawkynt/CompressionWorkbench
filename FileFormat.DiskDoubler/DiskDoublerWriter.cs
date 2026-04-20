#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.DiskDoubler;

/// <summary>
/// Writes a DiskDoubler stored (method 0) file. Takes a single data fork
/// payload; resource fork is empty. Roundtrips through
/// <see cref="DiskDoublerReader"/>.
/// </summary>
public sealed class DiskDoublerWriter {
  private const int HeaderSize = 82;
  private const int FileNameOffset = 34;
  private const int MaxNameLength = 47; // 82 - 34(offset) - 1(pascal len byte) = 47

  private string _name = "file";
  private byte[] _data = [];

  public void SetFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _name = name;
    _data = data;
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var hdr = new byte[HeaderSize];

    // [0..4] version — reader doesn't validate; use zero.
    // [4..8] Mac file type — "????" placeholder.
    Encoding.ASCII.GetBytes("????").CopyTo(hdr, 4);
    // [8..12] Mac creator — "????" placeholder.
    Encoding.ASCII.GetBytes("????").CopyTo(hdr, 8);
    // [12..14] Finder flags — 0.
    // [14..16] reserved — 0.

    // [16..20] data fork original size (BE).
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16), (uint)_data.Length);
    // [20..24] data fork compressed size (BE) — same as original (stored).
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20), (uint)_data.Length);
    // [24..28] resource fork original size — 0.
    // [28..32] resource fork compressed size — 0.
    // [32] data fork compression method — 0 (stored).
    hdr[32] = DiskDoublerConstants.MethodStored;
    // [33] resource fork compression method — 0.
    hdr[33] = DiskDoublerConstants.MethodStored;

    // [34] Pascal string: length byte + name bytes (Latin-1, up to 47 chars).
    var nameBytes = Encoding.Latin1.GetBytes(_name);
    var nameLen = Math.Min(nameBytes.Length, MaxNameLength);
    hdr[FileNameOffset] = (byte)nameLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(hdr.AsSpan(FileNameOffset + 1));

    output.Write(hdr);
    output.Write(_data);
  }
}
