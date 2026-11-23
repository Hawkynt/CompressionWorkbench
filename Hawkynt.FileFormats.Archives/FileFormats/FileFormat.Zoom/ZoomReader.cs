using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.Zoom;

/// <summary>
/// Reads an Amiga Zoom disk archive ("ZOM5", or the older "ZOOM") and rebuilds its floppy image.
/// </summary>
/// <remarks>
/// <para>Layout (big-endian). File header, 72 bytes then its check (CRC-32, reflected 0xEDB88320, initial
/// value 0, no final inversion, for "ZOM5"; a 2-byte CRC-16 for "ZOOM", see <see cref="Crc16Zoom"/>):</para>
/// <code>
///   0x00 magic   0x04 first cylinder   0x05 last cylinder   0x06 version   0x07 whole tracks only
///   0x08 flags (bit 0: every sector stored with its 16-byte label)          0x0C date stamp
///   0x18 attached text size   0x1C its packed size   0x20 note offset   0x24 encrypted   0x28 key
/// </code>
/// <para>An attached text, when present, follows: packed size bytes of LZHUF, then its check. Then
/// cylinder records, each a header and its payload. "ZOM5" record header (38 bytes + 4-byte check):
/// five cylinder numbers (0xFF = unused), a pad byte, five 22-bit sector masks, then packed length,
/// length after LZHUF (0 = no RLE stage), final length, LZHUF flag and the payload's CRC-32. "ZOOM"
/// records keep the same first 28 bytes, then packed length, RLE marker bytes, RLE input length,
/// final length, LZHUF flag and the payload's CRC-16, and a 2-byte check.</para>
/// <para>The payload is LZHUF (<see cref="ZoomLzhufDecoder"/>) and then run-length coded; its bytes are
/// the stored sectors of the record's cylinders, in slot order and ascending sector order (sector
/// labels follow each whole cylinder when flag bit 0 is set). Sectors and cylinders not stored are zero.
/// "ZOM5" runs: a 24-bit length, an escape byte, then <c>escape, 0</c> = the escape byte and
/// <c>escape, n, b</c> = n + 1 copies of b. "ZOOM" runs are read backwards from the end with two
/// marker bytes, one for repeated bytes and one for zeros.</para>
/// <para>Sourcing: layout and checks from libxad's Zoom client (The Unarchiver's XADMaster,
/// <c>libxad/clients/Zoom.c</c>, LGPL-2.1-or-later) and horsicq/XArchive <c>xzoomdecoder</c> (MIT);
/// verified on real archives, every check of which passes and whose AmigaDOS volumes come out intact.</para>
/// </remarks>
public sealed class ZoomReader {
  /// <summary>Bytes in one cylinder: 2 tracks × 11 sectors × 512 bytes.</summary>
  public const int CylinderSize = 22 * 512;

  /// <summary>Number of cylinders on a DD floppy.</summary>
  public const int Cylinders = 80;

  private const int HeaderSize = 72;
  private const int Slots = 5;
  private const int LabelSize = 16;

  private readonly byte[] _data;
  private readonly bool _old;
  private readonly int _recordsStart;

  /// <summary>Gets the first cylinder stored.</summary>
  public int FirstCylinder { get; }

  /// <summary>Gets the last cylinder stored.</summary>
  public int LastCylinder { get; }

  /// <summary>Gets whether the archive is password-encrypted.</summary>
  public bool IsEncrypted { get; }

  /// <summary>Gets whether each stored sector carries its 16-byte label.</summary>
  public bool HasSectorLabels { get; }

  /// <summary>Gets the attached text, or <see langword="null"/>.</summary>
  public byte[]? Text { get; }

  /// <summary>Gets whether this is the older "ZOOM" variant.</summary>
  public bool IsOldFormat => this._old;

  /// <summary>Parses and checks the header and the attached text.</summary>
  /// <exception cref="InvalidDataException">Not a Zoom archive, or the header fails its check.</exception>
  public ZoomReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    var d = this._data.AsSpan();
    if (d.Length < HeaderSize + 2)
      throw new InvalidDataException("Too short for a Zoom archive.");
    if (d[..4].SequenceEqual("ZOM5"u8))
      this._old = false;
    else if (d[..4].SequenceEqual("ZOOM"u8))
      this._old = true;
    else
      throw new InvalidDataException("Not a Zoom archive (missing \"ZOM5\"/\"ZOOM\").");

    var pos = HeaderSize;
    this.CheckBlock(0, HeaderSize, ref pos, "header");
    this.FirstCylinder = d[4];
    this.LastCylinder = d[5];
    if (this.LastCylinder < this.FirstCylinder || this.LastCylinder >= Cylinders)
      throw new InvalidDataException("Zoom cylinder range is invalid.");
    this.HasSectorLabels = (BinaryPrimitives.ReadUInt32BigEndian(d[8..]) & 1) != 0;
    this.IsEncrypted = d[0x24] != 0;

    var textSize = BinaryPrimitives.ReadUInt32BigEndian(d[0x18..]);
    var packedSize = BinaryPrimitives.ReadUInt32BigEndian(d[0x1C..]);
    if (textSize != 0) {
      if (packedSize > d.Length - pos)
        throw new InvalidDataException("Zoom attached text runs past the end.");
      var start = pos;
      pos += (int)packedSize;
      this.CheckBlock(start, (int)packedSize, ref pos, "attached text");
      if (!this.IsEncrypted)
        this.Text = ZoomLzhufDecoder.Decode(d.Slice(start, (int)packedSize), checked((int)textSize));
    }
    this._recordsStart = pos;
  }

  /// <summary>Returns whether <paramref name="header"/> starts a Zoom archive.</summary>
  public static bool IsZoom(ReadOnlySpan<byte> header) =>
    header.Length >= 4 && (header[..4].SequenceEqual("ZOM5"u8) || header[..4].SequenceEqual("ZOOM"u8));

  /// <summary>Rebuilds the 901,120-byte floppy image.</summary>
  /// <exception cref="NotSupportedException">The archive is password-encrypted.</exception>
  /// <exception cref="InvalidDataException">A record is damaged or fails its check.</exception>
  public byte[] ExtractDisk() {
    if (this.IsEncrypted)
      throw new NotSupportedException("Zoom archive is password-encrypted; decryption is not supported.");
    var image = new byte[Cylinders * CylinderSize];
    var d = this._data.AsSpan();
    var pos = this._recordsStart;
    const int recordHeader = 38;
    var doneThrough = -1;

    while (doneThrough < this.LastCylinder && pos + recordHeader <= d.Length) {
      var start = pos;
      pos += recordHeader;
      this.CheckBlock(start, recordHeader, ref pos, "record header");
      var rec = d.Slice(start, recordHeader);
      var packedLength = BinaryPrimitives.ReadUInt16BigEndian(rec[26..]);
      int middle, final, flag, markers = 0;
      uint payloadCheck;
      if (this._old) {
        markers = BinaryPrimitives.ReadUInt16BigEndian(rec[28..]);
        middle = BinaryPrimitives.ReadUInt16BigEndian(rec[30..]);
        flag = BinaryPrimitives.ReadUInt16BigEndian(rec[34..]);
        payloadCheck = BinaryPrimitives.ReadUInt16BigEndian(rec[36..]);
      } else {
        middle = BinaryPrimitives.ReadUInt16BigEndian(rec[28..]);
        flag = BinaryPrimitives.ReadUInt16BigEndian(rec[32..]);
        payloadCheck = BinaryPrimitives.ReadUInt32BigEndian(rec[34..]);
      }
      if (packedLength > d.Length - pos)
        throw new InvalidDataException("Zoom record payload runs past the end.");
      var payload = d.Slice(pos, packedLength);
      pos += packedLength;
      var actual = this._old ? Crc16Zoom(payload) : Crc32Zoom(payload);
      if (actual != payloadCheck)
        throw new InvalidDataException("Zoom record payload fails its check.");

      // The stored sectors decide the final length; the header's own field is not trusted.
      var sectorBytes = this.HasSectorLabels ? 512 + LabelSize : 512;
      final = 0;
      for (var i = 0; i < Slots; ++i)
        if (rec[i] != 0xFF)
          final += sectorBytes * int.PopCount((int)(BinaryPrimitives.ReadUInt32BigEndian(rec[(6 + 4 * i)..]) & 0x3FFFFF));

      var buffer = payload.ToArray();
      if (flag != 0)
        buffer = ZoomLzhufDecoder.Decode(buffer, middle != 0 ? middle : final);
      if (middle != 0)
        buffer = this._old ? RunDecodeOld(buffer, final, middle, markers) : RunDecode(buffer, final);
      if (buffer.Length < final)
        throw new InvalidDataException("Zoom record decodes to fewer bytes than its sectors need.");

      var source = 0;
      for (var i = 0; i < Slots; ++i) {
        int cylinder = rec[i];
        if (cylinder == 0xFF)
          continue;
        if (cylinder >= Cylinders)
          throw new InvalidDataException($"Zoom record names cylinder {cylinder}.");
        var mask = BinaryPrimitives.ReadUInt32BigEndian(rec[(6 + 4 * i)..]) & 0x3FFFFF;
        var target = image.AsSpan(cylinder * CylinderSize, CylinderSize);
        if (this.HasSectorLabels) {
          if (mask != 0x3FFFFF)
            throw new InvalidDataException("Zoom record with sector labels stores a partial cylinder.");
          buffer.AsSpan(source, CylinderSize).CopyTo(target);
          source += CylinderSize + 22 * LabelSize;
        } else {
          for (var k = 0; k < 22; ++k, mask >>= 1)
            if ((mask & 1) != 0) {
              buffer.AsSpan(source, 512).CopyTo(target.Slice(k * 512, 512));
              source += 512;
            }
        }
        doneThrough = Math.Max(doneThrough, cylinder);
      }
    }
    return image;
  }

  /// <summary>Reads the check that follows a block and compares it with the block's own.</summary>
  private void CheckBlock(int start, int length, ref int pos, string what) {
    var d = this._data.AsSpan();
    var block = d.Slice(start, length);
    if (this._old) {
      if (pos + 2 > d.Length)
        throw new InvalidDataException($"Zoom {what} check is missing.");
      if (BinaryPrimitives.ReadUInt16BigEndian(d[pos..]) != Crc16Zoom(block))
        throw new InvalidDataException($"Zoom {what} fails its check.");
      pos += 2;
    } else {
      if (pos + 4 > d.Length)
        throw new InvalidDataException($"Zoom {what} check is missing.");
      if (BinaryPrimitives.ReadUInt32BigEndian(d[pos..]) != Crc32Zoom(block))
        throw new InvalidDataException($"Zoom {what} fails its check.");
      pos += 4;
    }
  }

  /// <summary>CRC-32 with the reflected polynomial 0xEDB88320, initial value 0 and no final inversion.</summary>
  internal static uint Crc32Zoom(ReadOnlySpan<byte> data) {
    var crc = 0u;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; ++i)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
    }
    return crc;
  }

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table() {
    var t = new ushort[256];
    for (var i = 0; i < 256; ++i) {
      var c = i << 8;
      for (var k = 0; k < 8; ++k)
        c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x1021) & 0xFFFF : (c << 1) & 0xFFFF;
      t[i] = (ushort)c;
    }
    return t;
  }

  /// <summary>
  /// The "ZOOM" check: polynomial 0x1021, MSB first, initial 0, with each byte shifted in after the table
  /// step rather than XORed into the index (<c>crc = table[crc &gt;&gt; 8] ^ (crc &lt;&lt; 8 ^ byte)</c>).
  /// </summary>
  internal static ushort Crc16Zoom(ReadOnlySpan<byte> data) {
    var crc = 0;
    foreach (var b in data)
      crc = Crc16Table[(crc >> 8) & 0xFF] ^ (((crc << 8) & 0xFFFF) ^ b);
    return (ushort)crc;
  }

  private static byte[] RunDecode(byte[] src, int size) {
    if (src.Length < 4)
      throw new InvalidDataException("Zoom run-length block is shorter than its header.");
    var declared = (src[0] << 16) | (src[1] << 8) | src[2];
    if (declared != size)
      throw new InvalidDataException("Zoom run-length block states a different length.");
    var escape = src[3];
    var output = new byte[size];
    int s = 4, d = 0;
    while (d < size) {
      if (s >= src.Length)
        throw new InvalidDataException("Zoom run-length block ends early.");
      var b = src[s++];
      if (b != escape) {
        output[d++] = b;
        continue;
      }
      if (s >= src.Length)
        throw new InvalidDataException("Zoom run-length block ends early.");
      int count = src[s++];
      if (count == 0) {
        output[d++] = escape;
        continue;
      }
      if (s >= src.Length)
        throw new InvalidDataException("Zoom run-length block ends early.");
      var value = src[s++];
      count = Math.Min(count + 1, size - d);
      output.AsSpan(d, count).Fill(value);
      d += count;
    }
    return output;
  }

  private static byte[] RunDecodeOld(byte[] src, int size, int srcSize, int markers) {
    if (srcSize > src.Length)
      throw new InvalidDataException("Zoom run-length block is shorter than stated.");
    var repeat = (byte)(markers >> 8);
    var zero = (byte)markers;
    var output = new byte[size];
    int s = srcSize, d = size;
    while (d > 0) {
      if (s <= 0)
        throw new InvalidDataException("Zoom run-length block ends early.");
      var b = src[--s];
      if (b == repeat || b == zero) {
        if (s <= 0)
          throw new InvalidDataException("Zoom run-length block ends early.");
        int count = src[--s];
        if (count == 0)
          count = 0x100;
        byte value = 0;
        if (b == repeat) {
          if (s <= 0)
            throw new InvalidDataException("Zoom run-length block ends early.");
          value = src[--s];
        }
        if (count > d)
          throw new InvalidDataException("Zoom run-length block overruns its output.");
        output.AsSpan(d - count, count).Fill(value);
        d -= count;
      } else
        output[--d] = b;
    }
    return output;
  }
}
