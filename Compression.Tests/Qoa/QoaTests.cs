#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Codec.Qoa;
using Compression.Registry;
using FileFormat.Qoa;

namespace Compression.Tests.Qoa;

[TestFixture]
public class QoaTests {

  // ── Table pin tests ──────────────────────────────────────────────────────

  [Test]
  public void DequantTab_SpotValues_MatchReference() {
    // qoa_dequant_tab from qoa.h — pin a few corners/middles.
    Assert.That(QoaCodec.DequantTab[0][0], Is.EqualTo(1));
    Assert.That(QoaCodec.DequantTab[0][7], Is.EqualTo(-7));
    Assert.That(QoaCodec.DequantTab[8][2], Is.EqualTo(1053));
    Assert.That(QoaCodec.DequantTab[15][0], Is.EqualTo(1536));
    Assert.That(QoaCodec.DequantTab[15][7], Is.EqualTo(-14336));
    Assert.That(QoaCodec.DequantTab[7][6], Is.EqualTo(2128));
  }

  [Test]
  public void ScaleFactorTab_MatchesReference() {
    Assert.That(QoaCodec.ScaleFactorTab,
      Is.EqualTo(new[] { 1, 7, 21, 45, 84, 138, 211, 304, 421, 562, 731, 928, 1157, 1419, 1715, 2048 }));
  }

  // ── Hand-walked single-slice decode ────────────────────────────────────────

  // Builds a 1-channel QOA stream: file header + one frame whose LMS state is all
  // zero and whose single slice has scalefactor index 0 and all 20 residual indices
  // = 0. With zero weights, prediction is always 0; dequant[0][0] = 1; the LMS
  // update's delta (1>>4) is 0 so weights never move — every sample decodes to 1.
  private static byte[] BuildSingleSliceStream() {
    var data = new byte[8 + 8 + 16 + 8];
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), 0x716f6166);  // 'qoaf'
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 20);          // 20 samples/channel

    var frameSize = (ulong)(8 + 16 + 8);
    var frameHeader = ((ulong)1 << 56)           // channels
                    | ((ulong)44100 << 32)       // samplerate
                    | ((ulong)20 << 16)          // frame samples
                    | frameSize;
    BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8), frameHeader);
    // LMS state (16 bytes) left zero: history = 0, weights = 0.
    // Slice (8 bytes) left zero: scalefactor index 0, all residual indices 0.
    return data;
  }

  [Test]
  public void Decode_SingleZeroSlice_AllSamplesAreOne() {
    var stream = BuildSingleSliceStream();
    using var input = new MemoryStream(stream);
    using var output = new MemoryStream();
    QoaCodec.Decompress(input, output);
    var pcm = output.ToArray();

    Assert.That(pcm.Length, Is.EqualTo(20 * 2));
    for (var i = 0; i < 20; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), Is.EqualTo(1),
        $"sample {i}");
  }

  [Test]
  public void ReadStreamInfo_ReadsHeader() {
    var stream = BuildSingleSliceStream();
    using var input = new MemoryStream(stream);
    var info = QoaCodec.ReadStreamInfo(input);
    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.SamplesPerChannel, Is.EqualTo(20));
  }

  // ── Round trip ──────────────────────────────────────────────────────────────

  private static byte[] MakeStereoQoa(int frames = 6000) {
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(Math.Sin(i * 0.05) * 8000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(Math.Sin(i * 0.03) * 6000));
    }
    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    QoaCodec.Compress(input, output, 2, 44100);
    return output.ToArray();
  }

  [Test]
  public void Encode_Decode_IsDeterministicAndStable() {
    var qoa = MakeStereoQoa();

    using var in1 = new MemoryStream(qoa);
    using var pcm1 = new MemoryStream();
    QoaCodec.Decompress(in1, pcm1);

    // Re-encoding the decoded PCM and decoding again reproduces the same PCM
    // (QOA is fully deterministic).
    using var reEncIn = new MemoryStream(pcm1.ToArray());
    using var reEnc = new MemoryStream();
    QoaCodec.Compress(reEncIn, reEnc, 2, 44100);

    using var in2 = new MemoryStream(reEnc.ToArray());
    using var pcm2 = new MemoryStream();
    QoaCodec.Decompress(in2, pcm2);

    Assert.That(pcm2.ToArray(), Is.EqualTo(pcm1.ToArray()));
  }

  // ── Descriptor ──────────────────────────────────────────────────────────────

  [Test]
  public void Descriptor_ListsFullAndChannels_Stereo() {
    var qoa = MakeStereoQoa();
    using var ms = new MemoryStream(qoa);
    var entries = new QoaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.qoa" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_ExtractedChannelIsValidMonoWav() {
    var qoa = MakeStereoQoa();
    using var ms = new MemoryStream(qoa);
    using var output = new MemoryStream();
    new QoaFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
  }

  [Test]
  public void Descriptor_FullQoaIsByteExact() {
    var qoa = MakeStereoQoa();
    using var ms = new MemoryStream(qoa);
    using var output = new MemoryStream();
    new QoaFormatDescriptor().ExtractEntry(ms, "FULL.qoa", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(qoa));
  }

  [Test]
  public void Descriptor_GracefulFallback_OnGarbage() {
    var blob = new byte[16];
    using var ms = new MemoryStream(blob);
    var entries = new QoaFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.qoa"));
  }

  [Test]
  public void Create_PassesThroughFullQoa() {
    var qoa = MakeStereoQoa();
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.qoa", qoa) };
    using var output = new MemoryStream();
    new QoaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(qoa));
  }

  [Test]
  public void Create_AssemblesFromChannelWavs_DecodesEquivalently() {
    var qoa = MakeStereoQoa();
    var descriptor = new QoaFormatDescriptor();

    var inputs = new List<ArchiveInputInfo>();
    foreach (var name in new[] { "LEFT.wav", "RIGHT.wav" }) {
      using var src = new MemoryStream(qoa);
      using var chOut = new MemoryStream();
      descriptor.ExtractEntry(src, name, chOut, null);
      inputs.Add(ArchiveInputInfo.InMemory(name, chOut.ToArray()));
    }

    using var assembled = new MemoryStream();
    descriptor.Create(assembled, inputs, new FormatCreateOptions());

    using var origIn = new MemoryStream(qoa);
    using var origPcm = new MemoryStream();
    QoaCodec.Decompress(origIn, origPcm);

    using var newIn = new MemoryStream(assembled.ToArray());
    using var newPcm = new MemoryStream();
    QoaCodec.Decompress(newIn, newPcm);

    Assert.That(newPcm.ToArray(), Is.EqualTo(origPcm.ToArray()));
  }
}
