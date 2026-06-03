#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Brr;
using FileFormat.Spc;

namespace Compression.Tests.Spc;

[TestFixture]
public class SpcTests {

  private const int HasId666Offset = 0x23;
  private const int Id666Offset = 0x2E;
  private const int AramOffset = 0x100;
  private const int AramSize = 0x10000;
  private const int DspOffset = 0x10100;
  private const int DirRegister = 0x5D;
  private const int FileSize = 0x10180;

  private static readonly short[] PcmA = MakeRamp(48, 5000);
  private static readonly short[] PcmB = MakeRamp(32, -4000);

  private static short[] MakeRamp(int count, int amplitude) {
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i / 4.0) * amplitude);
    return pcm;
  }

  // Builds a 0x10180-byte SPC with ID666 text tags and an ARAM directory of two BRR chains.
  private static byte[] BuildSpc(byte dirPage, bool garbageDirectory = false) {
    var spc = new byte[FileSize];
    "SNES-SPC700 Sound File Data v0.30"u8.CopyTo(spc);
    spc[0x21] = 0x1A;
    spc[0x22] = 0x1A;
    spc[HasId666Offset] = 0x1A;   // ID666 present (text format)
    spc[0x24] = 30;               // version minor

    // ID666 text tags.
    WriteText(spc, Id666Offset + 0x00, "Boss Theme", 32);
    WriteText(spc, Id666Offset + 0x20, "Test Game", 32);
    WriteText(spc, Id666Offset + 0x40, "Dumper", 16);
    WriteText(spc, Id666Offset + 0x50, "Some comments", 32);
    WriteText(spc, 0xB1, "Composer", 32);

    // DSP DIR register.
    spc[DspOffset + DirRegister] = dirPage;

    var aram = spc.AsSpan(AramOffset, AramSize);

    if (garbageDirectory) {
      // Fill the directory page with junk that never forms a terminating BRR chain.
      for (var i = 0; i < 256; ++i)
        aram.Slice(dirPage * 0x100 + i, 1).Fill(0xAB);
      return spc;
    }

    // Encode two samples and place them in ARAM, then point directory entries at them.
    var brrA = BrrCodec.Encode(PcmA);
    var brrB = BrrCodec.Encode(PcmB);
    // Place sample data well away from the directory page (dirPage * 0x100).
    var addrA = 0x4000;
    var addrB = 0x4000 + brrA.Length;
    brrA.CopyTo(aram[addrA..]);
    brrB.CopyTo(aram[addrB..]);

    var dirBase = dirPage * 0x100;
    BinaryPrimitives.WriteUInt16LittleEndian(aram[dirBase..], (ushort)addrA);
    BinaryPrimitives.WriteUInt16LittleEndian(aram[(dirBase + 2)..], (ushort)addrA); // loop = start
    BinaryPrimitives.WriteUInt16LittleEndian(aram[(dirBase + 4)..], (ushort)addrB);
    BinaryPrimitives.WriteUInt16LittleEndian(aram[(dirBase + 6)..], (ushort)addrB);
    // Remaining slots stay zero → invalid run terminates the directory.

    return spc;
  }

  private static void WriteText(byte[] spc, int offset, string text, int length) {
    var bytes = Encoding.ASCII.GetBytes(text);
    Array.Copy(bytes, 0, spc, offset, Math.Min(bytes.Length, length - 1));
  }

  // ──────────────────────────────────────────────────────────────────────────

  [Test]
  public void List_SurfacesFullContainerAndTwoSamples() {
    using var ms = new MemoryStream(BuildSpc(dirPage: 0x20));
    var entries = new SpcFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.spc").Kind, Is.EqualTo("Container"));

    var samples = entries.Where(e => e.Kind == "Sample").ToList();
    Assert.That(samples.Count, Is.EqualTo(2), "two valid BRR chains surface");
    Assert.That(samples.Any(e => e.Name == "samples/00.wav"), Is.True);
    Assert.That(samples.Any(e => e.Name == "samples/01.wav"), Is.True);
  }

  [Test]
  public void ExtractedSample_DecodesToExactBrrPcm() {
    using var ms = new MemoryStream(BuildSpc(dirPage: 0x20));
    using var output = new MemoryStream();
    new SpcFormatDescriptor().ExtractEntry(ms, "samples/00.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(32000u));

    // The WAV payload must equal the decode of the exact BRR bytes we stored.
    var expected = BrrCodec.Decode(BrrCodec.Encode(PcmA));
    var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo(expected.Length * 2));
    for (var i = 0; i < expected.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)), Is.EqualTo(expected[i]), $"sample {i}");
  }

  [Test]
  public void Id666Tags_ParseIntoMetadataIni() {
    using var ms = new MemoryStream(BuildSpc(dirPage: 0x20));
    using var output = new MemoryStream();
    new SpcFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(ini, Does.Contain("song_title=Boss Theme"));
    Assert.That(ini, Does.Contain("game_title=Test Game"));
    Assert.That(ini, Does.Contain("artist=Composer"));
  }

  [Test]
  public void GarbageDirectory_DegradesToFullAndMetadataOnly() {
    using var ms = new MemoryStream(BuildSpc(dirPage: 0x20, garbageDirectory: true));
    var entries = new SpcFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.spc"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False, "no samples from a garbage directory");
  }

  [Test]
  public void ShortBlob_DegradesGracefully() {
    using var ms = new MemoryStream(new byte[0x100]); // signature region only, no ARAM/DSP
    var entries = new SpcFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.spc"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Sample"), Is.False);
  }
}
