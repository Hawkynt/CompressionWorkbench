using System.Buffers.Binary;

namespace FileFormat.Cdi;

/// <summary>
/// Regenerates the CD-ROM sector integrity fields needed after changing the
/// 2,048-byte filesystem payload inside a CDI track.
/// </summary>
/// <remarks>
/// Clean-room implementation from the ECMA-130 CD-ROM sector definition and
/// Annex A RSPC matrices, plus the ECMA-168 Mode-2 Form-1 field layout. The
/// lookup tables are generated from the standard-defined polynomials rather
/// than copied from another implementation.
/// </remarks>
internal static class CdiCdSectorIntegrity {
  internal const int UserDataSize = 2048;

  private const int RawSectorSize = 2352;
  private const int Mode1UserDataOffset = 16;
  private const int Mode1EdcOffset = 2064;
  private const int Mode1ReservedOffset = 2068;
  private const int Mode1ReservedLength = 8;
  private const int Mode2SubheaderOffset = 16;
  private const int Mode2UserDataOffset = 24;
  private const int Mode2EdcOffset = 2072;
  private const int PParityOffset = 2076;
  private const int PParitySize = 172;
  private const int QParityOffset = 2248;
  private const int QParitySize = 104;
  private const byte Mode2Form2Flag = 0x20;

  // ECMA-130 14.3: (x^16+x^15+x^2+1)(x^16+x^2+x+1), reflected for
  // the least-significant-bit-first serial CRC convention used on disc.
  private const uint ReflectedEdcPolynomial = 0xD8018001;

  // ECMA-130 Annex A: GF(2^8) primitive polynomial x^8+x^4+x^3+x^2+1.
  private const byte GfReduction = 0x1D;

  private static readonly uint[] EdcTable = BuildEdcTable();
  private static readonly byte[] DivideByAlphaPlusOne = BuildDivideByAlphaPlusOneTable();

  internal static bool Supports(CdiTrackInfo track) => track switch {
    { Mode: CdiTrackMode.Mode1, ReadMode: CdiReadMode.Mode1_2048, StoredSectorSize: UserDataSize } => true,
    { Mode: CdiTrackMode.Mode1, ReadMode: CdiReadMode.Raw2352 or CdiReadMode.Raw2352_Q16 or CdiReadMode.Raw2352_Pw96,
      StoredSectorSize: >= RawSectorSize } => true,
    { Mode: CdiTrackMode.Mode2, ReadMode: CdiReadMode.Mode2_2336, StoredSectorSize: 2336 } => true,
    { Mode: CdiTrackMode.Mode2, ReadMode: CdiReadMode.Raw2352 or CdiReadMode.Raw2352_Q16 or CdiReadMode.Raw2352_Pw96,
      StoredSectorSize: >= RawSectorSize } => true,
    _ => false,
  };

  internal static bool IsRewritableSector(CdiTrackInfo track, ReadOnlySpan<byte> storedSector) {
    if (!Supports(track) || storedSector.Length < track.StoredSectorSize)
      return false;

    return track.Mode switch {
      CdiTrackMode.Mode1 => track.ReadMode == CdiReadMode.Mode1_2048 ||
                            storedSector.Length >= RawSectorSize && storedSector[15] == 1,
      CdiTrackMode.Mode2 => IsMode2Form1(track, storedSector),
      _ => false,
    };
  }

  internal static byte[] GetUserData(CdiTrackInfo track, ReadOnlySpan<byte> storedSector) {
    var offset = GetUserDataOffset(track);
    if (offset < 0 || storedSector.Length < offset + UserDataSize)
      throw new InvalidDataException("CDI sector is too short to contain its 2,048-byte filesystem payload.");
    return storedSector.Slice(offset, UserDataSize).ToArray();
  }

  internal static void RewriteUserData(CdiTrackInfo track, Span<byte> storedSector, ReadOnlySpan<byte> userData) {
    if (userData.Length != UserDataSize)
      throw new ArgumentException("CD-ROM filesystem sectors require exactly 2,048 bytes of user data.", nameof(userData));
    if (!IsRewritableSector(track, storedSector))
      throw new InvalidDataException("CDI data sector is not a rewritable Mode-1 or Mode-2 Form-1 sector.");

    switch (track.ReadMode) {
      case CdiReadMode.Mode1_2048:
        userData.CopyTo(storedSector[..UserDataSize]);
        return;

      case CdiReadMode.Mode2_2336: {
        Span<byte> raw = stackalloc byte[RawSectorSize];
        raw.Clear();
        storedSector[..2336].CopyTo(raw[Mode2SubheaderOffset..]);
        raw[15] = 2;
        userData.CopyTo(raw.Slice(Mode2UserDataOffset, UserDataSize));
        RegenerateMode2Form1(raw);
        raw[Mode2SubheaderOffset..].CopyTo(storedSector[..2336]);
        return;
      }

      case CdiReadMode.Raw2352:
      case CdiReadMode.Raw2352_Q16:
      case CdiReadMode.Raw2352_Pw96: {
        var raw = storedSector[..RawSectorSize];
        if (track.Mode == CdiTrackMode.Mode1) {
          userData.CopyTo(raw.Slice(Mode1UserDataOffset, UserDataSize));
          RegenerateMode1(raw);
        } else {
          userData.CopyTo(raw.Slice(Mode2UserDataOffset, UserDataSize));
          RegenerateMode2Form1(raw);
        }
        return;
      }

      default:
        throw new NotSupportedException($"CDI read mode {track.ReadMode} cannot carry a rewritable ISO sector.");
    }
  }

  internal static void RegenerateMode1(Span<byte> rawSector) {
    RequireRawSector(rawSector);
    if (rawSector[15] != 1)
      throw new InvalidDataException("CD-ROM Mode-1 integrity regeneration requires sector mode byte 1.");

    BinaryPrimitives.WriteUInt32LittleEndian(rawSector.Slice(Mode1EdcOffset, sizeof(uint)),
      ComputeEdc(rawSector[..Mode1EdcOffset]));
    rawSector.Slice(Mode1ReservedOffset, Mode1ReservedLength).Clear();
    WriteEcc(rawSector, zeroAddressForParity: false);
  }

  internal static void RegenerateMode2Form1(Span<byte> rawSector) {
    RequireRawSector(rawSector);
    if (rawSector[15] != 2 || !IsRawMode2Form1(rawSector))
      throw new InvalidDataException("CD-ROM Mode-2 integrity regeneration requires a Form-1 sector.");

    BinaryPrimitives.WriteUInt32LittleEndian(rawSector.Slice(Mode2EdcOffset, sizeof(uint)),
      ComputeEdc(rawSector.Slice(Mode2SubheaderOffset, Mode2EdcOffset - Mode2SubheaderOffset)));
    WriteEcc(rawSector, zeroAddressForParity: true);
  }

  private static int GetUserDataOffset(CdiTrackInfo track) => (track.Mode, track.ReadMode) switch {
    (CdiTrackMode.Mode1, CdiReadMode.Mode1_2048) => 0,
    (CdiTrackMode.Mode1, CdiReadMode.Raw2352 or CdiReadMode.Raw2352_Q16 or CdiReadMode.Raw2352_Pw96) =>
      Mode1UserDataOffset,
    (CdiTrackMode.Mode2, CdiReadMode.Mode2_2336) => 8,
    (CdiTrackMode.Mode2, CdiReadMode.Raw2352 or CdiReadMode.Raw2352_Q16 or CdiReadMode.Raw2352_Pw96) =>
      Mode2UserDataOffset,
    _ => -1,
  };

  private static bool IsMode2Form1(CdiTrackInfo track, ReadOnlySpan<byte> storedSector) {
    var subheaderOffset = track.ReadMode == CdiReadMode.Mode2_2336 ? 0 : Mode2SubheaderOffset;
    if (storedSector.Length < subheaderOffset + 8)
      return false;
    if (track.ReadMode != CdiReadMode.Mode2_2336 &&
        (storedSector.Length < RawSectorSize || storedSector[15] != 2))
      return false;

    var firstSubheader = storedSector.Slice(subheaderOffset, 4);
    var secondSubheader = storedSector.Slice(subheaderOffset + 4, 4);
    return firstSubheader.SequenceEqual(secondSubheader) &&
           (firstSubheader[2] & Mode2Form2Flag) == 0;
  }

  private static bool IsRawMode2Form1(ReadOnlySpan<byte> rawSector) {
    if (rawSector.Length < RawSectorSize)
      return false;
    var firstSubheader = rawSector.Slice(Mode2SubheaderOffset, 4);
    return firstSubheader.SequenceEqual(rawSector.Slice(Mode2SubheaderOffset + 4, 4)) &&
           (firstSubheader[2] & Mode2Form2Flag) == 0;
  }

  private static void RequireRawSector(ReadOnlySpan<byte> rawSector) {
    if (rawSector.Length < RawSectorSize)
      throw new ArgumentException("CD-ROM integrity regeneration requires a complete 2,352-byte raw sector.", nameof(rawSector));
  }

  private static uint ComputeEdc(ReadOnlySpan<byte> bytes) {
    var result = 0u;
    foreach (var value in bytes)
      result = (result >> 8) ^ EdcTable[(byte)(result ^ value)];
    return result;
  }

  private static void WriteEcc(Span<byte> rawSector, bool zeroAddressForParity) {
    Span<byte> savedAddress = stackalloc byte[4];
    Span<byte> data = stackalloc byte[43];
    if (zeroAddressForParity) {
      rawSector.Slice(12, 4).CopyTo(savedAddress);
      rawSector.Slice(12, 4).Clear();
    }

    try {
      var rspc = rawSector[12..];

      // P parity: 43 columns, each a (26,24) shortened Reed-Solomon code.
      for (var column = 0; column < 43; ++column)
        for (var lane = 0; lane < 2; ++lane) {
          var symbols = data[..24];
          for (var row = 0; row < symbols.Length; ++row)
            symbols[row] = rspc[2 * (43 * row + column) + lane];

          ComputeTwoParitySymbols(symbols, out var first, out var second);
          rawSector[PParityOffset + 2 * column + lane] = first;
          rawSector[PParityOffset + PParitySize / 2 + 2 * column + lane] = second;
        }

      // Q parity: 26 diagonals, each a (45,43) shortened Reed-Solomon code.
      // The modulo-1118 word walk is the ECMA-130 Annex A Q matrix.
      for (var diagonal = 0; diagonal < 26; ++diagonal)
        for (var lane = 0; lane < 2; ++lane) {
          for (var row = 0; row < data.Length; ++row) {
            var word = (44 * row + 43 * diagonal) % 1118;
            data[row] = rspc[2 * word + lane];
          }

          ComputeTwoParitySymbols(data, out var first, out var second);
          rawSector[QParityOffset + 2 * diagonal + lane] = first;
          rawSector[QParityOffset + QParitySize / 2 + 2 * diagonal + lane] = second;
        }
    } finally {
      if (zeroAddressForParity)
        savedAddress.CopyTo(rawSector.Slice(12, 4));
    }
  }

  private static void ComputeTwoParitySymbols(ReadOnlySpan<byte> data, out byte first, out byte second) {
    // For the Annex-A parity-check matrix, s0 is the XOR of data symbols and
    // s1 is their alpha-weighted sum. Horner evaluation produces s1/alpha^2.
    byte s0 = 0;
    byte horner = 0;
    foreach (var value in data) {
      s0 ^= value;
      horner = (byte)(MultiplyByAlpha(horner) ^ value);
    }

    var s1 = MultiplyByAlpha(MultiplyByAlpha(horner));
    first = DivideByAlphaPlusOne[(byte)(s0 ^ s1)];
    second = (byte)(s0 ^ first);
  }

  private static byte MultiplyByAlpha(byte value)
    => (byte)((value << 1) ^ ((value & 0x80) != 0 ? GfReduction : 0));

  private static uint[] BuildEdcTable() {
    var result = new uint[256];
    for (var i = 0; i < result.Length; ++i) {
      var value = (uint)i;
      for (var bit = 0; bit < 8; ++bit)
        value = (value >> 1) ^ ((value & 1) != 0 ? ReflectedEdcPolynomial : 0);
      result[i] = value;
    }
    return result;
  }

  private static byte[] BuildDivideByAlphaPlusOneTable() {
    var result = new byte[256];
    for (var value = 0; value < 256; ++value) {
      var x = (byte)value;
      result[(byte)(x ^ MultiplyByAlpha(x))] = x;
    }
    return result;
  }
}
