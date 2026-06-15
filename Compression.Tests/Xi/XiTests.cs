#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Xi;

namespace Compression.Tests.Xi;

[TestFixture]
public class XiTests {

  private const int NumSamplesOffset = 0x10A;
  private const int SampleHeadersOffset = 0x10C;
  private const int SampleHeaderSize = 40;

  private static byte[] BuildXi(IReadOnlyList<(byte[] Data, bool Is16, sbyte RelNote, sbyte Finetune, string Name)> samples) {
    var headerEnd = SampleHeadersOffset + samples.Count * SampleHeaderSize;
    using var ms = new MemoryStream();

    var header = new byte[headerEnd];
    Encoding.ASCII.GetBytes("Extended Instrument: ").CopyTo(header, 0); // 21 bytes
    Encoding.ASCII.GetBytes("TestInstr").CopyTo(header, 21);
    header[43] = 0x1A;
    Encoding.ASCII.GetBytes("FT2").CopyTo(header, 44);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(64), 0x0102);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(NumSamplesOffset), (ushort)samples.Count);

    for (var i = 0; i < samples.Count; ++i) {
      var off = SampleHeadersOffset + i * SampleHeaderSize;
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(off), (uint)samples[i].Data.Length);
      header[off + 12] = 64;                                  // volume
      header[off + 13] = unchecked((byte)samples[i].Finetune);
      header[off + 14] = (byte)(samples[i].Is16 ? 0x10 : 0x00); // type
      header[off + 16] = unchecked((byte)samples[i].RelNote);
      Encoding.ASCII.GetBytes(samples[i].Name).CopyTo(header, off + 18);
    }
    ms.Write(header);
    foreach (var s in samples) ms.Write(s.Data);
    return ms.ToArray();
  }

  /// <summary>FT2 8-bit delta encode: store successive differences of the running sum.</summary>
  private static byte[] Delta8(byte[] signed) {
    var r = new byte[signed.Length];
    byte prev = 0;
    for (var i = 0; i < signed.Length; ++i) {
      r[i] = unchecked((byte)(signed[i] - prev));
      prev = signed[i];
    }
    return r;
  }

  private static byte[] Delta16(short[] samples) {
    var r = new byte[samples.Length * 2];
    short prev = 0;
    for (var i = 0; i < samples.Length; ++i) {
      var d = unchecked((short)(samples[i] - prev));
      BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(i * 2), d);
      prev = samples[i];
    }
    return r;
  }

  [Test]
  public void Delta8_RoundTripsThroughDecoder() {
    // signed running samples 0,10,-10,127 → deltas → decode back.
    var signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    var encoded = Delta8(signed);
    var decoded = XiFormatDescriptor.DecodeDelta8(encoded);
    Assert.That(decoded, Is.EqualTo(signed));
  }

  [Test]
  public void Delta16_RoundTripsThroughDecoder() {
    var samples = new short[] { 0, 1000, -2000, 30000 };
    var encoded = Delta16(samples);
    var decoded = XiFormatDescriptor.DecodeDelta16(encoded);
    var back = new short[samples.Length];
    for (var i = 0; i < samples.Length; ++i)
      back[i] = BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(i * 2));
    Assert.That(back, Is.EqualTo(samples));
  }

  [Test]
  public void Lists_FullMetadataAndSampleWav() {
    var signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    var xi = BuildXi([(Delta8(signed), false, 0, 0, "Kick")]);

    using var ms = new MemoryStream(xi);
    var entries = new XiFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.xi").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    var sample = entries.First(e => e.Name.StartsWith("samples/00_"));
    Assert.That(sample.Kind, Is.EqualTo("Sample"));
  }

  [Test]
  public void Sample8Bit_DecodesToUnsignedWavWithExpectedBytes() {
    // RelativeNote 0, finetune 0 → rate exactly 8363.
    var signed = new byte[] { 0, 10, unchecked((byte)-10), 127 };
    var xi = BuildXi([(Delta8(signed), false, 0, 0, "Kick")]);

    using var ms = new MemoryStream(xi);
    var name = new XiFormatDescriptor().List(ms, null).First(e => e.Name.StartsWith("samples/")).Name;

    using var output = new MemoryStream();
    new XiFormatDescriptor().ExtractEntry(new MemoryStream(xi), name, output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));        // 8-bit
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8363u));    // C-4 rate

    // signed {0,10,-10,127} → unsigned {128,138,118,255}.
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(new byte[] { 128, 138, 118, 255 }));
  }

  [Test]
  public void Sample16Bit_DecodesToSignedWav() {
    var samples = new short[] { 0, 1000, -2000, 30000 };
    var xi = BuildXi([(Delta16(samples), true, 12, 0, "Lead")]); // +12 semitones → 2× rate

    using var ms = new MemoryStream(xi);
    var name = new XiFormatDescriptor().List(ms, null).First(e => e.Name.StartsWith("samples/")).Name;

    using var output = new MemoryStream();
    new XiFormatDescriptor().ExtractEntry(new MemoryStream(xi), name, output, null);
    var wav = output.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(16726u)); // 8363 * 2

    var pcm = wav.AsSpan(44);
    var back = new short[samples.Length];
    for (var i = 0; i < samples.Length; ++i)
      back[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
    Assert.That(back, Is.EqualTo(samples));
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    var truncated = "Extended Instrument: short"u8.ToArray();
    using var ms = new MemoryStream(truncated);
    var entries = new XiFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.xi"));
  }
}
