using System.Buffers.Binary;
using System.Security.Cryptography;
using FileFormat.Cdi;

namespace Compression.Tests.Cdi;

[TestFixture]
public sealed class CdiCdSectorIntegrityTests {
  private const int RawSectorSize = 2352;

  [Test, Category("KnownAnswer")]
  public void Mode1_Ecma130KnownAnswer_MatchesDerivedSectorVector() {
    var sector = BuildMode1Vector();

    CdiCdSectorIntegrity.RegenerateMode1(sector);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(2064, 4)), Is.EqualTo(0x6266D9ACu));
      Assert.That(sector.AsSpan(2068, 8).ToArray(), Is.All.Zero);
      Assert.That(Convert.ToHexString(SHA256.HashData(sector)).ToLowerInvariant(),
        Is.EqualTo("b2218e4d7e47957959a561a77bee15e452a95fde1b1d6c2efb39833577ac65d8"));
      Assert.That(sector[2076], Is.EqualTo(0xDE));
      Assert.That(sector[2161], Is.EqualTo(0x86));
      Assert.That(sector[2162], Is.EqualTo(0xD1));
      Assert.That(sector[2248], Is.EqualTo(0xDA));
      Assert.That(sector[2351], Is.EqualTo(0x20));
    });
  }

  [Test, Category("KnownAnswer")]
  public void Mode2Form1_Ecma130KnownAnswer_MatchesDerivedSectorVector() {
    var sector = BuildMode2Form1Vector();
    var originalHeader = sector.AsSpan(12, 4).ToArray();
    var originalSubheader = sector.AsSpan(16, 8).ToArray();

    CdiCdSectorIntegrity.RegenerateMode2Form1(sector);

    Assert.Multiple(() => {
      Assert.That(sector.AsSpan(12, 4).ToArray(), Is.EqualTo(originalHeader),
        "Mode-2 address/mode bytes must be restored after zero-address ECC calculation");
      Assert.That(sector.AsSpan(16, 8).ToArray(), Is.EqualTo(originalSubheader));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(2072, 4)), Is.EqualTo(0x83CF72ECu));
      Assert.That(Convert.ToHexString(SHA256.HashData(sector)).ToLowerInvariant(),
        Is.EqualTo("174daf7b00cc79528f1859d4e36b1f91f2044295d7c917205a5af669d03e261e"));
      Assert.That(sector[2076], Is.EqualTo(0xE7));
      Assert.That(sector[2161], Is.EqualTo(0xA7));
      Assert.That(sector[2162], Is.EqualTo(0xCC));
      Assert.That(sector[2248], Is.EqualTo(0x9C));
      Assert.That(sector[2351], Is.EqualTo(0x28));
    });
  }

  [TestCase(CdiReadMode.Raw2352_Q16, 2368)]
  [TestCase(CdiReadMode.Raw2352_Pw96, 2448)]
  [Category("RoundTrip")]
  public void RawMode1Rewrite_PreservesAppendedSubchannel(CdiReadMode readMode, int stride) {
    var stored = new byte[stride];
    var raw = BuildMode1Vector();
    CdiCdSectorIntegrity.RegenerateMode1(raw);
    raw.CopyTo(stored, 0);
    stored.AsSpan(RawSectorSize).Fill(0xA5);
    var replacement = Enumerable.Range(0, 2048).Select(static i => (byte)(i * 11 + 3)).ToArray();
    var track = Track(CdiTrackMode.Mode1, readMode, stride);

    CdiCdSectorIntegrity.RewriteUserData(track, stored, replacement);

    Assert.Multiple(() => {
      Assert.That(CdiCdSectorIntegrity.GetUserData(track, stored), Is.EqualTo(replacement));
      Assert.That(stored.AsSpan(RawSectorSize).ToArray(), Is.All.EqualTo(0xA5));
      Assert.That(stored[15], Is.EqualTo(1));
    });
  }

  [Test, Category("RoundTrip")]
  public void CookedMode2Form1Rewrite_RegeneratesIntegrityAndPreservesSubheader() {
    var raw = BuildMode2Form1Vector();
    CdiCdSectorIntegrity.RegenerateMode2Form1(raw);
    var stored = raw.AsSpan(16).ToArray();
    var subheader = stored.AsSpan(0, 8).ToArray();
    var replacement = Enumerable.Range(0, 2048).Select(static i => (byte)(255 - i)).ToArray();
    var track = Track(CdiTrackMode.Mode2, CdiReadMode.Mode2_2336, 2336);

    CdiCdSectorIntegrity.RewriteUserData(track, stored, replacement);

    Assert.Multiple(() => {
      Assert.That(CdiCdSectorIntegrity.GetUserData(track, stored), Is.EqualTo(replacement));
      Assert.That(stored.AsSpan(0, 8).ToArray(), Is.EqualTo(subheader));
      Assert.That(stored.AsSpan(2056, 4).ToArray(), Is.Not.All.Zero);
      Assert.That(stored.AsSpan(2060, 276).ToArray(), Is.Not.All.Zero);
    });
  }

  [Test, Category("ErrorPath")]
  public void Mode2Form2_IsRejectedBeforeMutation() {
    var raw = BuildMode2Form1Vector();
    raw[18] |= 0x20;
    raw[22] |= 0x20;
    var before = raw.ToArray();
    var track = Track(CdiTrackMode.Mode2, CdiReadMode.Raw2352, RawSectorSize);

    Assert.Multiple(() => {
      Assert.That(CdiCdSectorIntegrity.IsRewritableSector(track, raw), Is.False);
      Assert.Throws<InvalidDataException>(() =>
        CdiCdSectorIntegrity.RewriteUserData(track, raw, new byte[2048]));
      Assert.That(raw, Is.EqualTo(before));
    });
  }

  private static byte[] BuildMode1Vector() {
    var sector = new byte[RawSectorSize];
    WriteSync(sector);
    sector[12] = 0x00;
    sector[13] = 0x02;
    sector[14] = 0x00;
    sector[15] = 1;
    for (var i = 0; i < 2048; ++i)
      sector[16 + i] = unchecked((byte)(i * 73 + 19));
    return sector;
  }

  private static byte[] BuildMode2Form1Vector() {
    var sector = new byte[RawSectorSize];
    WriteSync(sector);
    sector[12] = 0x00;
    sector[13] = 0x02;
    sector[14] = 0x10;
    sector[15] = 2;
    byte[] subheader = [1, 2, 8, 0, 1, 2, 8, 0];
    subheader.CopyTo(sector, 16);
    for (var i = 0; i < 2048; ++i)
      sector[24 + i] = unchecked((byte)(i * 29 + 7));
    return sector;
  }

  private static void WriteSync(Span<byte> sector) {
    sector[0] = 0;
    sector.Slice(1, 10).Fill(0xFF);
    sector[11] = 0;
  }

  private static CdiTrackInfo Track(CdiTrackMode mode, CdiReadMode readMode, int stride)
    => new(
      SessionNumber: 1,
      TrackNumber: 1,
      Mode: mode,
      ReadMode: readMode,
      PregapSectors: 0,
      SectorCount: 1,
      DataSectorCount: 1,
      StartLba: 0,
      StoredSectorSize: stride,
      Control: 4,
      FileOffset: 0,
      DataOffset: 0
    );
}
