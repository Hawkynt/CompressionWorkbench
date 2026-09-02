#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aomei;

/// <summary>
/// <c>BR_IMAGE_FILE_TAIL</c> — the 0x674-byte struct at offset
/// <c>file_size - 0x674</c>. Layout per <c>docs/AOMEI_FORMAT_SPEC.md</c> §2.2,
/// sourced from <c>ammntdrv.sys!FUN_0001601c</c>.
///
/// <para>
/// Only the first 12 bytes (Flag / Size / Crc32) are verifiably decoded — the
/// remaining 0x668 bytes are <b>TODO</b> per spec §10.1 (likely index offset,
/// total payload size and back-pointer to head). The reader exposes the raw
/// body bytes via <see cref="BodyRaw"/>; the writer fills it with zeros and
/// seals the CRC.
/// </para>
/// </summary>
public readonly struct BrFileTail {

  /// <summary><c>'BIFT'</c> magic — must equal <see cref="AomeiConstants.BiftFlag"/>.</summary>
  public readonly uint Flag;

  /// <summary>Struct size — must equal <see cref="AomeiConstants.BiftSize"/>.</summary>
  public readonly uint Size;

  /// <summary>CRC32 over the whole tail with this field zeroed.</summary>
  public readonly uint Crc32;

  /// <summary>Raw 0x668 bytes of opaque body after the standard header.
  /// Field layout is TODO per spec §10.1.</summary>
  public readonly byte[] BodyRaw;

  /// <summary>
  /// Initializes a new instance of <see cref="BrFileTail"/>.
  /// </summary>
public BrFileTail(uint flag, uint size, uint crc32, byte[] bodyRaw) {
    this.Flag = flag;
    this.Size = size;
    this.Crc32 = crc32;
    this.BodyRaw = bodyRaw ?? throw new ArgumentNullException(nameof(bodyRaw));
  }

  /// <summary>True when Flag and Size match the spec values.</summary>
  public bool MagicAndSizeValid =>
    this.Flag == AomeiConstants.BiftFlag && this.Size == AomeiConstants.BiftSize;

  /// <summary>Reads the tail from the last <see cref="AomeiConstants.BiftSize"/>
  /// bytes of <paramref name="image"/>.</summary>
  public static BrFileTail Read(ReadOnlySpan<byte> image) {
    if (image.Length < AomeiConstants.BiftSize)
      throw new ArgumentException(
        $"Buffer too small for BR_IMAGE_FILE_TAIL ({image.Length} < {AomeiConstants.BiftSize}).",
        nameof(image));
    var slice = image[^AomeiConstants.BiftSize..];
    var flag = BinaryPrimitives.ReadUInt32LittleEndian(slice);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(slice[4..]);
    var crc = BinaryPrimitives.ReadUInt32LittleEndian(slice[8..]);
    var body = slice[12..].ToArray();
    return new BrFileTail(flag, size, crc, body);
  }

  /// <summary>Builds a fresh tail with all-zero body and the CRC sealed in
  /// place. Returns the 0x674-byte buffer ready to write at file offset
  /// <c>file_size - 0x674</c>.</summary>
  public static byte[] BuildEmpty() {
    var buf = new byte[AomeiConstants.BiftSize];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, AomeiConstants.BiftFlag);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)AomeiConstants.BiftSize);
    BrStandardHeader.SealCrc(buf);
    return buf;
  }
}
