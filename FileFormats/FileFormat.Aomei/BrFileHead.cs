#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aomei;

/// <summary>
/// <c>BR_IMAGE_FILE_HEAD</c> — the 0x65C-byte struct at offset 0 of every
/// <c>.adi</c>/<c>.afi</c> image. Layout per <c>docs/AOMEI_FORMAT_SPEC.md</c>
/// §2.1, sourced from <c>ammntdrv.sys!FUN_00015e90</c>.
///
/// <para>
/// Only the first 12 bytes (Flag / Size / Crc32) are verifiably decoded — the
/// remaining 0x650 bytes are <b>TODO</b> per spec §10.1 (likely backup GUID,
/// version, BR_IMAGE_INFO descriptors). The reader exposes the raw body bytes
/// via <see cref="BodyRaw"/> for future RE work; the writer fills it with
/// zeros and seals the CRC.
/// </para>
///
/// <para>
/// The Flag field reuses the <see cref="AomeiConstants.BifhFlag"/>
/// <c>0x48464942</c> ("BIFH" LE) — the same four bytes as
/// <see cref="AomeiConstants.BifhMagicAscii"/>[0..4] which the descriptor
/// uses for offset-0 magic detection. The trailing 0x5C ("\\") byte that
/// public sources document as part of the family magic is therefore the
/// low byte of <c>Size</c> (0x65C = 1628 = 0x_00_00_06_5C LE) — not an
/// extra magic byte. The reader still tolerates samples whose low-byte
/// happens to differ (would be rejected at the Size check), keeping the
/// 5-byte ASCII detection that has shipped since the R/O baseline.
/// </para>
/// </summary>
public readonly struct BrFileHead {

  /// <summary><c>'BIFH'</c> magic — must equal <see cref="AomeiConstants.BifhFlag"/>.</summary>
  public readonly uint Flag;

  /// <summary>Struct size — must equal <see cref="AomeiConstants.BifhSize"/>.</summary>
  public readonly uint Size;

  /// <summary>CRC32 over the whole head with this field zeroed.</summary>
  public readonly uint Crc32;

  /// <summary>Raw 0x650 bytes of opaque body after the standard header.
  /// Field layout is TODO per spec §10.1.</summary>
  public readonly byte[] BodyRaw;

  /// <summary>
  /// Initializes a new instance of <see cref="BrFileHead"/>.
  /// </summary>
public BrFileHead(uint flag, uint size, uint crc32, byte[] bodyRaw) {
    this.Flag = flag;
    this.Size = size;
    this.Crc32 = crc32;
    this.BodyRaw = bodyRaw ?? throw new ArgumentNullException(nameof(bodyRaw));
  }

  /// <summary>True when Flag and Size match the spec values.</summary>
  public bool MagicAndSizeValid =>
    this.Flag == AomeiConstants.BifhFlag && this.Size == AomeiConstants.BifhSize;

  /// <summary>Reads the head from the first <see cref="AomeiConstants.BifhSize"/>
  /// bytes of <paramref name="image"/>.</summary>
  public static BrFileHead Read(ReadOnlySpan<byte> image) {
    if (image.Length < AomeiConstants.BifhSize)
      throw new ArgumentException(
        $"Buffer too small for BR_IMAGE_FILE_HEAD ({image.Length} < {AomeiConstants.BifhSize}).",
        nameof(image));
    var flag = BinaryPrimitives.ReadUInt32LittleEndian(image);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(image[4..]);
    var crc = BinaryPrimitives.ReadUInt32LittleEndian(image[8..]);
    var body = image[12..AomeiConstants.BifhSize].ToArray();
    return new BrFileHead(flag, size, crc, body);
  }

  /// <summary>Builds a fresh head with all-zero body (the recovered field
  /// layout is incomplete — see spec §10.1) and the CRC sealed in place.
  /// Returns the 0x65C-byte buffer ready to write at file offset 0.</summary>
  public static byte[] BuildEmpty() {
    var buf = new byte[AomeiConstants.BifhSize];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, AomeiConstants.BifhFlag);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)AomeiConstants.BifhSize);
    // Crc32 at +8 left zero, then sealed.
    BrStandardHeader.SealCrc(buf);
    return buf;
  }
}
