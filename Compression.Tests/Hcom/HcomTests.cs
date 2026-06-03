#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Hcom;

namespace Compression.Tests.Hcom;

[TestFixture]
public class HcomTests {

  /// <summary>
  /// Hand-builds a minimal HCOM fork with a two-leaf tree:
  ///   node 0 (root): leftson=1, rightson=2
  ///   node 1 (leaf): leftson=-1, rightson=deltaA
  ///   node 2 (leaf): leftson=-1, rightson=deltaB
  /// so bit 0 → deltaA, bit 1 → deltaB. The decoder seeds sample=0 and accumulates.
  /// </summary>
  private static byte[] BuildHcom(short deltaA, short deltaB, uint bitstreamWord, int sampleCount, int divisor) {
    using var ms = new MemoryStream();
    var u32 = new byte[4];
    var u16 = new byte[2];

    void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(u32, v); ms.Write(u32); }
    void S16(short v) { BinaryPrimitives.WriteInt16BigEndian(u16, v); ms.Write(u16); }
    void U16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(u16, v); ms.Write(u16); }
    void Node(short l, short r) { S16(l); S16(r); }

    ms.Write("HCOM"u8.ToArray());
    U32((uint)sampleCount);
    U32(0);                 // checksum
    U32(1);                 // delta
    U32((uint)divisor);
    U16(3);                 // dictsize
    Node(1, 2);             // root
    Node(-1, deltaA);       // leaf A (bit 0)
    Node(-1, deltaB);       // leaf B (bit 1)
    ms.WriteByte(0);        // padding
    U32(bitstreamWord);
    return ms.ToArray();
  }

  [Test]
  public void TreeWalk_AccumulatesDeltasMsbFirst() {
    // bits: 0,1,0,1 (then zeros). deltaA=+10 (bit0), deltaB=+5 (bit1).
    // seed 0 → +10=10 → +5=15 → +10=25 → +5=30.
    var word = 0b0101u << 28;   // 0,1,0,1 in the top nibble
    var hcom = BuildHcom(10, 5, word, sampleCount: 4, divisor: 1);

    using var output = new MemoryStream();
    new HcomFormatDescriptor().ExtractEntry(new MemoryStream(hcom), "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(22050u)); // 22050/1
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(new byte[] { 10, 15, 25, 30 }));
  }

  [Test]
  public void Divisor_SelectsRate() {
    var hcom = BuildHcom(1, 2, 0u, sampleCount: 1, divisor: 2);
    using var output = new MemoryStream();
    new HcomFormatDescriptor().ExtractEntry(new MemoryStream(hcom), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(11025u)); // 22050/2
  }

  [Test]
  public void Lists_FullMetadataAndMonoChannel() {
    var hcom = BuildHcom(10, 5, 0b0101u << 28, sampleCount: 4, divisor: 1);
    using var ms = new MemoryStream(hcom);
    var entries = new HcomFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.hcom").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Create_RoundTripsArbitraryPcm() {
    // A varied 8-bit unsigned waveform.
    var pcm = new byte[64];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (byte)((i * 37 + 11) & 0xFF);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 22050, 8);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
    using var created = new MemoryStream();
    new HcomFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var hcom = created.ToArray();

    Assert.That(hcom.AsSpan(0, 4).ToArray(), Is.EqualTo("HCOM"u8.ToArray()));

    using var back = new MemoryStream();
    new HcomFormatDescriptor().ExtractEntry(new MemoryStream(hcom), "MONO.wav", back, null);
    var decoded = back.ToArray().AsSpan(44).ToArray();
    Assert.That(decoded, Is.EqualTo(pcm));
  }

  [Test]
  public void Create_RoundTripsConstantSignal() {
    // Degenerate: a single repeated value exercises the one-symbol tree path.
    var pcm = new byte[16];
    Array.Fill(pcm, (byte)200);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 22050, 8);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
    using var created = new MemoryStream();
    new HcomFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    using var back = new MemoryStream();
    new HcomFormatDescriptor().ExtractEntry(new MemoryStream(created.ToArray()), "MONO.wav", back, null);
    Assert.That(back.ToArray().AsSpan(44).ToArray(), Is.EqualTo(pcm));
  }

  [Test]
  public void Truncated_FallsBackToFullOnly() {
    var junk = "HCOM"u8.ToArray(); // too short for the header
    using var ms = new MemoryStream(junk);
    var entries = new HcomFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.hcom"));
  }
}
