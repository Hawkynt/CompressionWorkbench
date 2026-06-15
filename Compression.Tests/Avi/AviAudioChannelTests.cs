#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Avi;

namespace Compression.Tests.Avi;

/// <summary>
/// Behaviour tests for AVI per-track audio channel extraction: each <c>auds</c> stream is
/// decoded to <c>TRACKn_&lt;CHANNEL&gt;.wav</c> entries (Kind Channel) while the raw track
/// entry is always preserved. PCM channels are byte-checked against the source samples.
/// </summary>
[TestFixture]
public class AviAudioChannelTests {

  /// <summary>Builds an AVI with a single PCM (wFormatTag 0x0001) stereo 16-bit audio stream.</summary>
  private static byte[] MakePcmAvi(byte[] interleaved, int channels, int sampleRate, int bits, int formatTag = 0x0001) {
    var avih = new byte[56];
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(24), 1); // one stream

    var strh = new byte[56];
    "auds"u8.CopyTo(strh.AsSpan(0));

    // WAVEFORMATEX
    var strf = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(0), (ushort)formatTag);
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(2), (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(4), (uint)sampleRate);
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(12), (ushort)(channels * bits / 8)); // block align
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(14), (ushort)bits);

    var strl = BuildList("strl", BuildChunk("strh", strh), BuildChunk("strf", strf));
    var hdrl = BuildList("hdrl", BuildChunk("avih", avih), strl);
    var movi = BuildList("movi", BuildChunk("00wb", interleaved));

    return WrapRiff(hdrl, movi);
  }

  /// <summary>Builds an AVI with a single MS-ADPCM (0x0002) mono stream carrying one block.</summary>
  private static byte[] MakeMsAdpcmAvi(byte[] block, int blockAlign, int sampleRate) {
    var avih = new byte[56];
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(24), 1);

    var strh = new byte[56];
    "auds"u8.CopyTo(strh.AsSpan(0));

    var strf = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(0), 0x0002);
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(2), 1); // mono
    BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(4), (uint)sampleRate);
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(12), (ushort)blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(14), 4);

    var strl = BuildList("strl", BuildChunk("strh", strh), BuildChunk("strf", strf));
    var hdrl = BuildList("hdrl", BuildChunk("avih", avih), strl);
    var movi = BuildList("movi", BuildChunk("00wb", block));
    return WrapRiff(hdrl, movi);
  }

  private static byte[] WrapRiff(byte[] hdrl, byte[] movi) {
    using var mem = new MemoryStream();
    mem.Write("RIFF"u8);
    var inner = new byte[4 + hdrl.Length + movi.Length];
    "AVI "u8.CopyTo(inner.AsSpan(0));
    hdrl.CopyTo(inner.AsSpan(4));
    movi.CopyTo(inner.AsSpan(4 + hdrl.Length));
    var size = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)inner.Length);
    mem.Write(size);
    mem.Write(inner);
    return mem.ToArray();
  }

  private static byte[] BuildChunk(string id, byte[] body) {
    var aligned = body.Length + (body.Length & 1);
    var chunk = new byte[8 + aligned];
    Encoding.ASCII.GetBytes(id).CopyTo(chunk.AsSpan(0));
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)body.Length);
    body.CopyTo(chunk.AsSpan(8));
    return chunk;
  }

  private static byte[] BuildList(string listType, params byte[][] children) {
    var bodyLen = 4 + children.Sum(c => c.Length);
    var chunk = new byte[8 + bodyLen];
    "LIST"u8.CopyTo(chunk.AsSpan(0));
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)bodyLen);
    Encoding.ASCII.GetBytes(listType).CopyTo(chunk.AsSpan(8));
    var off = 12;
    foreach (var c in children) { c.CopyTo(chunk.AsSpan(off)); off += c.Length; }
    return chunk;
  }

  /// <summary>Extracts the raw PCM data section (after the 44-byte canonical header) of a mono WAV blob.</summary>
  private static byte[] WavData(byte[] wav) => wav.AsSpan(44).ToArray();

  // ── Given a stereo PCM AVI, When listed, Then TRACK0_LEFT/RIGHT channels appear ──

  [Test]
  public void StereoPcm_ProducesLeftAndRightChannels() {
    // Two stereo frames: L=0x1111, R=0x2222 / L=0x3333, R=0x4444 (16-bit LE).
    var interleaved = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 };
    var blob = MakePcmAvi(interleaved, channels: 2, sampleRate: 48000, bits: 16);

    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Count, Is.EqualTo(2));
    Assert.That(channels.Any(e => e.Name == "TRACK0_LEFT.wav"), Is.True);
    Assert.That(channels.Any(e => e.Name == "TRACK0_RIGHT.wav"), Is.True);
  }

  [Test]
  public void StereoPcm_LeftChannelMatchesSourceSamples() {
    var interleaved = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 };
    var blob = MakePcmAvi(interleaved, channels: 2, sampleRate: 44100, bits: 16);

    using var ms = new MemoryStream(blob);
    var desc = new AviFormatDescriptor();
    desc.List(ms, null); // warm
    using var output = new MemoryStream();
    ms.Position = 0;
    desc.ExtractEntry(ms, "TRACK0_LEFT.wav", output, null);
    var left = WavData(output.ToArray());

    Assert.That(left, Is.EqualTo(new byte[] { 0x11, 0x11, 0x33, 0x33 }));
  }

  [Test]
  public void StereoPcm_RightChannelMatchesSourceSamples() {
    var interleaved = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 };
    var blob = MakePcmAvi(interleaved, channels: 2, sampleRate: 44100, bits: 16);

    using var ms = new MemoryStream(blob);
    var desc = new AviFormatDescriptor();
    using var output = new MemoryStream();
    ms.Position = 0;
    desc.ExtractEntry(ms, "TRACK0_RIGHT.wav", output, null);
    var right = WavData(output.ToArray());

    Assert.That(right, Is.EqualTo(new byte[] { 0x22, 0x22, 0x44, 0x44 }));
  }

  [Test]
  public void MonoPcm_ProducesSingleMonoChannel() {
    var interleaved = new byte[] { 0x01, 0x02, 0x03, 0x04 };
    var blob = MakePcmAvi(interleaved, channels: 1, sampleRate: 8000, bits: 16);

    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Count, Is.EqualTo(1));
    Assert.That(channels[0].Name, Is.EqualTo("TRACK0_MONO.wav"));
  }

  [Test]
  public void RawTrackEntry_IsAlwaysPreserved() {
    var interleaved = new byte[] { 0x11, 0x11, 0x22, 0x22 };
    var blob = MakePcmAvi(interleaved, channels: 2, sampleRate: 48000, bits: 16);
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    // The PCM track is surfaced as a playable WAV track entry, plus FULL container.
    Assert.That(entries.Any(e => e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Track" && e.Name.Contains("track_00_audio")), Is.True);
  }

  [Test]
  public void Metadata_RecordsCodecForPcm() {
    var interleaved = new byte[] { 0x11, 0x11, 0x22, 0x22 };
    var blob = MakePcmAvi(interleaved, channels: 2, sampleRate: 48000, bits: 16);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AviFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("track0_codec=pcm"));
  }

  [Test]
  public void UnsupportedCodec_FallsBackToRawAndRecordsReason() {
    // wFormatTag 0x00FF (raw AAC in AVI) is not on the decode list → raw fallback.
    var blob = MakePcmAvi(new byte[] { 1, 2, 3, 4 }, channels: 2, sampleRate: 48000, bits: 16, formatTag: 0x00FF);
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
    using var output = new MemoryStream();
    ms.Position = 0;
    new AviFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("track0_decode=unsupported"));
  }

  [Test]
  public void MsAdpcm_DecodesToMonoChannel() {
    // One MS-ADPCM mono block: 7-byte header (predictor 0, delta, sample1, sample2)
    // followed by nibble bytes. Exact PCM values aren't pinned here — the contract is
    // that the path decodes to a single playable mono channel.
    var blockAlign = 32;
    var block = new byte[blockAlign];
    block[0] = 0;                                    // predictor index 0
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(1), 16);   // delta
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(3), 100);  // sample1
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(5), 50);   // sample2
    // remaining bytes are ADPCM nibbles (left as zero).

    var blob = MakeMsAdpcmAvi(block, blockAlign, sampleRate: 22050);
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Count, Is.EqualTo(1));
    Assert.That(channels[0].Name, Is.EqualTo("TRACK0_MONO.wav"));
  }
}
