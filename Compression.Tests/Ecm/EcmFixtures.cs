using FileFormat.Cdi;

namespace Compression.Tests.Ecm;

/// <summary>Builds raw CD images whose sectors carry valid ECMA-130 EDC/ECC.</summary>
internal static class EcmFixtures {
  private static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

  private static byte Bcd(int value) => (byte)(value / 10 << 4 | value % 10);

  private static void WriteHeader(byte[] raw, int lba, byte mode) {
    Sync.CopyTo(raw, 0);
    var frames = lba + 150;
    raw[12] = Bcd(frames / (60 * 75));
    raw[13] = Bcd(frames / 75 % 60);
    raw[14] = Bcd(frames % 75);
    raw[15] = mode;
  }

  /// <summary>A Mode 1 sector with its EDC and ECC regenerated.</summary>
  public static byte[] Mode1(int lba, Random random) {
    var raw = new byte[2352];
    WriteHeader(raw, lba, 1);
    random.NextBytes(raw.AsSpan(16, 2048));
    CdiCdSectorIntegrity.RegenerateMode1(raw);
    return raw;
  }

  /// <summary>A Mode 2 Form 1 sector (raw, 2 352 bytes).</summary>
  public static byte[] Mode2Form1(int lba, Random random) {
    var raw = new byte[2352];
    WriteHeader(raw, lba, 2);
    byte[] subheader = [0x01, 0x02, 0x08, 0x00];
    subheader.CopyTo(raw, 16);
    subheader.CopyTo(raw, 20);
    random.NextBytes(raw.AsSpan(24, 2048));
    CdiCdSectorIntegrity.RegenerateMode2Form1(raw);
    return raw;
  }

  /// <summary>A Mode 2 Form 2 sector (raw, 2 352 bytes) with its optional EDC present.</summary>
  public static byte[] Mode2Form2(int lba, Random random) {
    var raw = new byte[2352];
    WriteHeader(raw, lba, 2);
    byte[] subheader = [0x01, 0x03, 0x64, 0x00];
    subheader.CopyTo(raw, 16);
    subheader.CopyTo(raw, 20);
    random.NextBytes(raw.AsSpan(24, 2324));
    var edc = CdiCdSectorIntegrity.ComputeEdc(raw.AsSpan(16, 0x91C));
    BitConverter.TryWriteBytes(raw.AsSpan(16 + 0x91C, 4), edc);
    return raw;
  }

  /// <summary>
  /// A mixed image: Mode 1 run, a damaged Mode 1 sector (must survive as literals), Mode 2
  /// Form 1 and Form 2 runs, an all-zero sector, and a ragged tail.
  /// </summary>
  public static byte[] MixedImage(int seed = 1234) {
    var random = new Random(seed);
    using var ms = new MemoryStream();
    var lba = 0;
    for (var i = 0; i < 6; ++i) ms.Write(Mode1(lba++, random));
    var damaged = Mode1(lba++, random);
    damaged[100] ^= 0x40;
    ms.Write(damaged);
    for (var i = 0; i < 4; ++i) ms.Write(Mode2Form1(lba++, random));
    for (var i = 0; i < 3; ++i) ms.Write(Mode2Form2(lba++, random));
    ms.Write(new byte[2352]);
    var tail = new byte[777];
    random.NextBytes(tail);
    ms.Write(tail);
    return ms.ToArray();
  }
}
