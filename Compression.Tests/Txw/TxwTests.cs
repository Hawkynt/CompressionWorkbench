#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Txw;

namespace Compression.Tests.Txw;

[TestFixture]
public class TxwTests {

  private const int HeaderSize = 32;
  private const int RateCodeOffset = 26;

  private static byte[] BuildTxw(byte[] packed, byte rateCode) {
    var blob = new byte[HeaderSize + packed.Length];
    "LM8953"u8.CopyTo(blob);
    blob[RateCodeOffset] = rateCode;
    packed.CopyTo(blob, HeaderSize);
    return blob;
  }

  [Test]
  public void Decode12Bit_UnpacksThreeBytesIntoTwoSignExtendedSamples() {
    // b0=0x12 b1=0x34 b2=0x56 → s1 = 0x123, s2 = 0x456 (both positive 12-bit).
    var packed = new byte[] { 0x12, 0x34, 0x56 };
    var pcm = TxwFormatDescriptor.Decode12Bit(packed);
    var s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0));
    var s2 = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2));
    Assert.That(s1, Is.EqualTo((short)(0x123 << 4)));
    Assert.That(s2, Is.EqualTo((short)(0x456 << 4)));
  }

  [Test]
  public void Decode12Bit_SignExtendsNegativeSamples() {
    // s1 = 0xFFF (-1 in 12-bit) → -1 << 4 = -16; s2 = 0x800 (most negative) → -2048<<4.
    var packed = new byte[] { 0xFF, 0xF8, 0x00 };
    var pcm = TxwFormatDescriptor.Decode12Bit(packed);
    var s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0));
    var s2 = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2));
    Assert.That(s1, Is.EqualTo((short)(-1 << 4)));
    Assert.That(s2, Is.EqualTo((short)(-2048 << 4)));
  }

  [Test]
  public void Lists_FullMetadataAndMonoChannel() {
    var txw = BuildTxw(new byte[] { 0x12, 0x34, 0x56 }, rateCode: 1);
    using var ms = new MemoryStream(txw);
    var entries = new TxwFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.txw").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    var mono = entries.First(e => e.Name == "MONO.wav");
    Assert.That(mono.Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void RateCode_SelectsExpectedRate() {
    var txw = BuildTxw(new byte[] { 0x10, 0x00, 0x00 }, rateCode: 2); // 50000 Hz
    using var output = new MemoryStream();
    new TxwFormatDescriptor().ExtractEntry(new MemoryStream(txw), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(50000u));
  }

  [Test]
  public void Create_FromMonoWav_RoundTripsThroughPackUnpack() {
    // Samples chosen as multiples of 16 so 12-bit truncation is lossless.
    var src = new short[] { 0x120 << 0, 0x450 << 0, unchecked((short)0xF000), 0x7FF0 };
    var pcm = new byte[src.Length * 2];
    for (var i = 0; i < src.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), src[i]);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 33333, 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
    using var created = new MemoryStream();
    new TxwFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var txw = created.ToArray();

    Assert.That(txw.AsSpan(0, 6).ToArray(), Is.EqualTo("LM8953"u8.ToArray()));
    Assert.That(TxwFormatDescriptor.RateFromCode(txw[RateCodeOffset]), Is.EqualTo(33333));

    // Re-read and confirm samples survive (high 12 bits preserved).
    using var back = new MemoryStream();
    new TxwFormatDescriptor().ExtractEntry(new MemoryStream(txw), "MONO.wav", back, null);
    var decoded = back.ToArray().AsSpan(44);
    for (var i = 0; i < src.Length; ++i) {
      var v = BinaryPrimitives.ReadInt16LittleEndian(decoded.Slice(i * 2, 2));
      // 12-bit round trip keeps the top 12 bits.
      Assert.That((short)(v & unchecked((short)0xFFF0)), Is.EqualTo((short)(src[i] & unchecked((short)0xFFF0))));
    }
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    var truncated = "LM8953"u8.ToArray();
    using var ms = new MemoryStream(truncated);
    var entries = new TxwFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.txw"));
  }
}
