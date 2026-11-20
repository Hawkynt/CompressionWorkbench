using System.Buffers.Binary;
using System.Security.Cryptography;
using Crc32 = Compression.Core.Checksums.Crc32;

namespace FileFormat.DvdisasterEcc;

/// <summary>
/// Writes dvdisaster RS01 error-correction files as <c>docs/DVDISASTER-ECC-ON-DISK.md</c> describes
/// them: the 4 KiB header, one CRC per 2048-byte image sector, and Reed-Solomon parity over GF(2^8)
/// (polynomial 0x187, first root 112, primitive element 11) computed across ndata = 255 − nroots
/// layers of the image.
/// </summary>
public static class DvdisasterEccWriter {
  private const int Sector = 2048;
  private const int FingerprintSector = 16;

  /// <summary>Parity bytes per codeword when none are asked for (dvdisaster's "normal" redundancy).</summary>
  public const int DefaultRoots = 32;

  private static readonly byte[] Alpha = new byte[512];
  private static readonly int[] Index = new int[256];

  static DvdisasterEccWriter() {
    var b = 1;
    for (var i = 0; i < 255; ++i) {
      Alpha[i] = Alpha[i + 255] = (byte)b;
      Index[b] = i;
      b <<= 1;
      if ((b & 0x100) != 0) b ^= 0x187;
    }
    Alpha[510] = Alpha[0];
    Alpha[511] = Alpha[1];
    Index[0] = 255;
  }

  /// <summary>Creates the RS01 ecc file of the image <paramref name="image"/>.</summary>
  /// <param name="image">The image (at least one byte).</param>
  /// <param name="roots">Parity bytes per codeword, 8 to 100: as many lost sectors per codeword can be rebuilt.</param>
  /// <param name="creatorVersion">The version number written as the creator's; by default the version the file needs.</param>
  public static byte[] CreateRs01(ReadOnlySpan<byte> image, int roots = DefaultRoots, int? creatorVersion = null) {
    if (image.IsEmpty)
      throw new ArgumentException("dvdisaster ecc: an empty image has nothing to protect.", nameof(image));
    if (roots is < 8 or > 100)
      throw new ArgumentOutOfRangeException(nameof(roots), "dvdisaster ecc: RS01 takes 8 to 100 roots.");
    var ndata = 255 - roots;
    var sectors = (image.Length + Sector - 1L) / Sector;
    var inLast = (int)(image.Length - (sectors - 1) * Sector);
    var layer = (sectors + ndata - 1) / ndata;
    var bodyLength = 4 * sectors + roots * layer * Sector;
    if (DvdisasterEccHeader.Size + bodyLength > int.MaxValue)
      throw new NotSupportedException("dvdisaster ecc: files over 2 GiB are not written.");
    var file = new byte[DvdisasterEccHeader.Size + bodyLength];

    // sector CRCs
    var padded = new byte[Sector];
    for (var s = 0L; s < sectors; ++s)
      BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan((int)(DvdisasterEccHeader.Size + 4 * s)), ~Crc32.Compute(SectorOf(image, s, padded)));

    // parity: for every sector offset in a layer, the 2048 codewords across the layers at once
    var generator = Generator(roots);
    var parityAt = (int)(DvdisasterEccHeader.Size + 4 * sectors);
    var registers = new byte[Sector * roots];
    var data = new byte[Sector];
    for (var j = 0L; j < layer; ++j) {
      Array.Clear(registers);
      for (var k = 0; k < ndata; ++k) {
        var s = k * layer + j;
        if (s < sectors) SectorOf(image, s, padded).CopyTo(data);
        else Array.Clear(data);
        for (var b = 0; b < Sector; ++b)
          Feed(registers.AsSpan(b * roots, roots), data[b], generator);
      }
      registers.CopyTo(file, parityAt + (int)(j * Sector * roots));
    }

    // header
    var h = file.AsSpan(0, DvdisasterEccHeader.Size);
    "*dvdisaster*RS01"u8.CopyTo(h);
    h[16] = 1;
    if (sectors > FingerprintSector) // an image without the fingerprint sector records no fingerprint
      MD5.HashData(SectorOf(image, FingerprintSector, padded)).CopyTo(h[20..]);
    MD5.HashData(image).CopyTo(h[36..]);
    MD5.HashData(file.AsSpan(DvdisasterEccHeader.Size)).CopyTo(h[52..]);
    BinaryPrimitives.WriteInt64LittleEndian(h[68..], sectors);
    BinaryPrimitives.WriteInt32LittleEndian(h[76..], ndata);
    BinaryPrimitives.WriteInt32LittleEndian(h[80..], roots);
    var needed = inLast == Sector ? 5500 : 6600;
    BinaryPrimitives.WriteInt32LittleEndian(h[84..], creatorVersion ?? needed);
    BinaryPrimitives.WriteInt32LittleEndian(h[88..], needed);
    BinaryPrimitives.WriteInt32LittleEndian(h[92..], FingerprintSector);
    BinaryPrimitives.WriteInt32LittleEndian(h[116..], inLast);
    return file;
  }

  /// <summary>The version RS03 files require (0.79).</summary>
  public const int Rs03NeededVersion = 7900;

  /// <summary>Method flag 0 bit of an RS03 error-correction file (as opposed to an augmented image).</summary>
  internal const byte Rs03EccFileFlag = 0x02;

  /// <summary>The value the self-CRC field holds while the CRC over the header or CRC block is taken.</summary>
  internal const uint SelfCrcSeed = 0x4C5047;

  /// <summary>Byte offsets of the fields of an RS03 CRC block, the 2048-byte sectors of the CRC layer.</summary>
  internal static class CrcBlock {
    public const int Cookie = 1024, Method = 1036, Flags = 1040, CreatorVersion = 1044, NeededVersion = 1048,
      FingerprintSector = 1052, Fingerprint = 1056, MediumMd5 = 1072, DataSectors = 1088, InLast = 1096,
      DataBytes = 1100, EccBytes = 1104, SectorsPerLayer = 1112, SelfCrc = 1120;
  }

  /// <summary>
  /// Creates the RS03 ecc file of the image <paramref name="image"/> (docs/DVDISASTER-ECC-ON-DISK.md, RS03): the
  /// 4 KiB header, the CRC layer and <paramref name="roots"/> parity layers.
  /// </summary>
  /// <param name="image">The image (at least one byte).</param>
  /// <param name="roots">Parity layers, 8 to 170: as many lost sectors per codeword can be rebuilt.</param>
  /// <param name="creatorVersion">The version number written as the creator's; by default the version the file needs.</param>
  /// <param name="developmentBuild">Whether to set the flag dvdisaster's development builds set (method flag byte 3).</param>
  public static byte[] CreateRs03(ReadOnlySpan<byte> image, int roots = DefaultRoots, int? creatorVersion = null, bool developmentBuild = false) {
    if (image.IsEmpty)
      throw new ArgumentException("dvdisaster ecc: an empty image has nothing to protect.", nameof(image));
    if (roots is < 8 or > 170)
      throw new ArgumentOutOfRangeException(nameof(roots), "dvdisaster ecc: RS03 takes 8 to 170 roots.");
    var ndata = 255 - roots;             // data layers plus the CRC layer
    var sectors = (image.Length + Sector - 1L) / Sector;
    var inLast = (int)(image.Length - (sectors - 1) * Sector);
    var layer = (sectors + ndata - 2) / (ndata - 1);
    var length = DvdisasterEccHeader.Size + (roots + 1L) * layer * Sector;
    if (length > int.MaxValue)
      throw new NotSupportedException("dvdisaster ecc: files over 2 GiB are not written.");
    var file = new byte[length];

    var padded = new byte[Sector];
    var fingerprint = sectors > FingerprintSector ? MD5.HashData(SectorOf(image, FingerprintSector, padded)) : new byte[16];
    var flags = new byte[] { Rs03EccFileFlag, 0, 0, (byte)(developmentBuild ? 1 : 0) };
    var creator = creatorVersion ?? Rs03NeededVersion;

    // A data-layer sector: the image's, or past its end a padding sector.
    var paddingSector = new byte[Sector];
    ReadOnlySpan<byte> DataSector(ReadOnlySpan<byte> img, long s) {
      if (s < sectors) return SectorOf(img, s, padded);
      PaddingSector(paddingSector, s, fingerprint);
      return paddingSector;
    }

    // CRC layer: block j holds the CRCs of sector j + 1 of every data layer (sector 0 for the last block).
    var crcAt = DvdisasterEccHeader.Size;
    for (var j = 0L; j < layer; ++j) {
      var block = file.AsSpan((int)(crcAt + j * Sector), Sector);
      var next = j + 1 < layer ? j + 1 : 0;
      for (var k = 0; k < ndata - 1; ++k)
        BinaryPrimitives.WriteUInt32LittleEndian(block[(4 * k)..], ~Crc32.Compute(DataSector(image, k * layer + next)));
      "*dvdisaster*RS03"u8.CopyTo(block[CrcBlock.Cookie..]);
      flags.CopyTo(block[CrcBlock.Flags..]);
      BinaryPrimitives.WriteInt32LittleEndian(block[CrcBlock.CreatorVersion..], creator);
      BinaryPrimitives.WriteInt32LittleEndian(block[CrcBlock.NeededVersion..], Rs03NeededVersion);
      BinaryPrimitives.WriteInt32LittleEndian(block[CrcBlock.FingerprintSector..], FingerprintSector);
      fingerprint.CopyTo(block[CrcBlock.Fingerprint..]);
      BinaryPrimitives.WriteInt64LittleEndian(block[CrcBlock.DataSectors..], sectors);
      BinaryPrimitives.WriteInt32LittleEndian(block[CrcBlock.InLast..], inLast);
      BinaryPrimitives.WriteInt32LittleEndian(block[CrcBlock.DataBytes..], ndata);
      BinaryPrimitives.WriteInt32LittleEndian(block[CrcBlock.EccBytes..], roots);
      BinaryPrimitives.WriteInt64LittleEndian(block[CrcBlock.SectorsPerLayer..], layer);
      SealSelfCrc(block, CrcBlock.SelfCrc);
    }

    // Parity: codeword (j, b) is byte b of sector j of every data layer, then of CRC block j; parity
    // layer i holds byte i of each codeword's parity.
    var generator = Generator(roots);
    var registers = new byte[Sector * roots];
    var data = new byte[Sector];
    var parityAt = crcAt + layer * Sector;
    for (var j = 0L; j < layer; ++j) {
      Array.Clear(registers);
      for (var k = 0; k < ndata; ++k) {
        if (k < ndata - 1) DataSector(image, k * layer + j).CopyTo(data);
        else file.AsSpan((int)(crcAt + j * Sector), Sector).CopyTo(data);
        for (var b = 0; b < Sector; ++b)
          Feed(registers.AsSpan(b * roots, roots), data[b], generator);
      }
      for (var i = 0; i < roots; ++i) {
        var slice = file.AsSpan((int)(parityAt + ((long)i * layer + j) * Sector), Sector);
        for (var b = 0; b < Sector; ++b)
          slice[b] = registers[b * roots + i];
      }
    }

    // header
    var h = file.AsSpan(0, DvdisasterEccHeader.Size);
    "*dvdisaster*RS03"u8.CopyTo(h);
    flags.CopyTo(h[16..]);
    fingerprint.CopyTo(h[20..]);
    BinaryPrimitives.WriteInt64LittleEndian(h[68..], sectors);
    BinaryPrimitives.WriteInt32LittleEndian(h[76..], ndata);
    BinaryPrimitives.WriteInt32LittleEndian(h[80..], roots);
    BinaryPrimitives.WriteInt32LittleEndian(h[84..], creator);
    BinaryPrimitives.WriteInt32LittleEndian(h[88..], Rs03NeededVersion);
    BinaryPrimitives.WriteInt32LittleEndian(h[92..], FingerprintSector);
    BinaryPrimitives.WriteInt32LittleEndian(h[116..], inLast);
    BinaryPrimitives.WriteInt64LittleEndian(h[120..], layer);
    SealSelfCrc(h, 96);
    return file;
  }

  /// <summary>Writes the self CRC: the CRC (without final inversion) of the whole span with the field set to the seed.</summary>
  internal static void SealSelfCrc(Span<byte> span, int at) {
    BinaryPrimitives.WriteUInt32LittleEndian(span[at..], SelfCrcSeed);
    BinaryPrimitives.WriteUInt32LittleEndian(span[at..], ~Crc32.Compute(span));
  }

  /// <summary>Whether a header or CRC block still has the self CRC it records.</summary>
  internal static bool SelfCrcHolds(ReadOnlySpan<byte> span, int at) {
    var copy = span.ToArray();
    var recorded = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(at));
    SealSelfCrc(copy, at);
    return BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(at)) == recorded;
  }

  /// <summary>The padding sector that stands for sector <paramref name="sector"/> past the end of the image.</summary>
  internal static void PaddingSector(Span<byte> buffer, long sector, ReadOnlySpan<byte> fingerprint) {
    buffer.Clear();
    static void Put(Span<byte> b, int at, string text) => System.Text.Encoding.ASCII.GetBytes(text, b[at..]);
    Put(buffer, 0, "dvdisaster padding sector       This is a padding sector needed for augmenting the image with error correction data.");
    Put(buffer, 2047 - 36, "dvdisaster padding sector end marker");
    Put(buffer, 0x100, "Padding sector marker version");
    Put(buffer, 0x120, "1.00");
    Put(buffer, 0x140, "Padding sector number");
    Put(buffer, 0x160, sector.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Put(buffer, 0x180, "Medium fingerprint");
    fingerprint.CopyTo(buffer[0x1A0..]);
    Put(buffer, 0x1C0, "Medium fingerprint sector");
    Put(buffer, 0x1E0, "16");
  }

  /// <summary>Sector <paramref name="s"/> of the image, zero-padded (all zeros past its end).</summary>
  private static ReadOnlySpan<byte> SectorOf(ReadOnlySpan<byte> image, long s, byte[] buffer) {
    var start = s * Sector;
    if (start + Sector <= image.Length)
      return image.Slice((int)start, Sector);
    Array.Clear(buffer);
    if (start < image.Length)
      image[(int)start..].CopyTo(buffer);
    return buffer;
  }

  /// <summary>The generator polynomial's coefficients as logarithms, lowest power first.</summary>
  private static int[] Generator(int roots) {
    var g = new int[roots + 1];
    g[0] = 1;
    for (int i = 0, root = 112 * 11; i < roots; ++i, root += 11) {
      g[i + 1] = 1;
      for (var j = i; j > 0; --j)
        g[j] = g[j] != 0 ? g[j - 1] ^ Alpha[(Index[g[j]] + root) % 255] : g[j - 1];
      g[0] = Alpha[(Index[g[0]] + root) % 255];
    }
    for (var i = 0; i <= roots; ++i) g[i] = Index[g[i]];
    return g;
  }

  /// <summary>Shifts one data byte into a systematic encoder's parity register (highest power first).</summary>
  private static void Feed(Span<byte> parity, byte value, int[] generator) {
    var roots = parity.Length;
    var feedback = Index[value ^ parity[0]];
    if (feedback != 255) {
      for (var j = 1; j < roots; ++j)
        parity[j] ^= Alpha[(feedback + generator[roots - j]) % 255];
    }
    parity[1..].CopyTo(parity);
    parity[roots - 1] = feedback != 255 ? Alpha[(feedback + generator[0]) % 255] : (byte)0;
  }
}
