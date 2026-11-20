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
