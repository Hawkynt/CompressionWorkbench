#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ZxScl;

/// <summary>
/// Reader for ZX Spectrum <c>.scl</c> archives.
/// </summary>
/// <remarks>
/// Layout:
/// <list type="number">
///   <item>8 bytes: ASCII magic "SINCLAIR".</item>
///   <item>1 byte: number of file headers (N, 0-255).</item>
///   <item>N x 14 bytes: TR-DOS header per file (8-byte name + type char + 2-byte param1
///   + 2-byte param2 + 1-byte length-in-sectors).</item>
///   <item>Concatenated raw file data: sum of (LengthSectors * 256) bytes.</item>
///   <item>4 bytes: CRC32 of everything before (little-endian). Not validated; kept for
///   round-trip awareness.</item>
/// </list>
/// </remarks>
public sealed class ZxSclReader : IDisposable {

  public const int SectorSize = 256;
  public const int HeaderSize = 14;
  /// <summary>Upper bound on payload size before CRC: 40 tracks x 16 sectors x 256 bytes x 4-layer.</summary>
  public const int MaxPayloadSize = 655360;

  /// <summary>"SINCLAIR" magic bytes.</summary>
  public static readonly byte[] Magic = [0x53, 0x49, 0x4E, 0x43, 0x4C, 0x41, 0x49, 0x52];

  private readonly byte[] _data;
  private readonly List<ZxSclEntry> _entries = [];

  public IReadOnlyList<ZxSclEntry> Entries => _entries;

  public ZxSclReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  public ZxSclReader(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _data = data;
    Parse();
  }

  private void Parse() {
    if (_data.Length < Magic.Length + 1 + 4)
      throw new InvalidDataException("SCL: file too small.");

    for (var i = 0; i < Magic.Length; i++)
      if (_data[i] != Magic[i])
        throw new InvalidDataException("SCL: missing SINCLAIR magic.");

    var count = _data[8];
    var headerEnd = 9 + count * HeaderSize;
    if (headerEnd + 4 > _data.Length)
      throw new InvalidDataException("SCL: truncated header table.");

    var dataCursor = (long)headerEnd;

    for (var i = 0; i < count; i++) {
      var ho = 9 + i * HeaderSize;

      // 8-byte name: ASCII, space-padded (0x20). Spectrum files sometimes use trailing 0x00.
      var nameBuf = new byte[8];
      Buffer.BlockCopy(_data, ho, nameBuf, 0, 8);
      var nameLen = 8;
      while (nameLen > 0 && (nameBuf[nameLen - 1] == 0x20 || nameBuf[nameLen - 1] == 0x00))
        nameLen--;
      var baseName = Encoding.ASCII.GetString(nameBuf, 0, nameLen);

      var fileType = (char)_data[ho + 8];
      var param1 = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ho + 9));
      var param2 = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(ho + 11));
      var lenSectors = _data[ho + 13];

      var dataSize = (long)lenSectors * SectorSize;
      if (dataCursor + dataSize > _data.Length - 4)
        throw new InvalidDataException($"SCL: entry {i} data overflows file (offset {dataCursor}, size {dataSize}).");

      var displayName = fileType switch {
        'B' => baseName + ".bas",
        'C' => baseName + ".cod",
        'D' => baseName + ".dat",
        '#' => baseName + ".seq",
        _ => baseName + "." + (char.IsLetterOrDigit(fileType) ? fileType : '_'),
      };

      _entries.Add(new ZxSclEntry {
        Name = displayName,
        Size = dataSize,
        FileType = fileType,
        Param1 = param1,
        Param2 = param2,
        LengthSectors = lenSectors,
        DataOffset = dataCursor,
      });

      dataCursor += dataSize;
    }
  }

  public byte[] Extract(ZxSclEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var off = (int)entry.DataOffset;
    var len = (int)entry.Size;
    if (len <= 0) return [];
    if (off < 0 || off + len > _data.Length) return [];
    var result = new byte[len];
    Buffer.BlockCopy(_data, off, result, 0, len);
    return result;
  }

  public void Dispose() { }
}
