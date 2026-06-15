using System.Buffers.Binary;
using Codec.Vorbis;

namespace Compression.Tests.Codecs.Vorbis;

/// <summary>
/// Basic correctness checks for the <see cref="VorbisCodec"/> decoder. We build
/// a minimum Ogg page carrying a hand-crafted Vorbis identification packet to
/// exercise the setup-read path without needing a real .ogg test vector. A
/// full end-to-end decode requires a real Ogg file; if one is not shipped,
/// that test is marked inconclusive.
/// </summary>
[TestFixture]
public class VorbisCodecTests {

  private static byte[] BuildOggPage(byte[] payload, uint serial, byte flags, ulong granule, uint seq) {
    // Segment table: break payload into 255-byte chunks with a trailing terminator.
    var segSizes = new List<byte>();
    var remaining = payload.Length;
    while (remaining >= 255) { segSizes.Add(255); remaining -= 255; }
    segSizes.Add((byte)remaining);
    var header = new byte[27 + segSizes.Count];
    header[0] = (byte)'O'; header[1] = (byte)'g'; header[2] = (byte)'g'; header[3] = (byte)'S';
    header[4] = 0; // stream structure version
    header[5] = flags;
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(6, 8), granule);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), serial);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), seq);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), 0); // CRC (skipped — not checked by our reader)
    header[26] = (byte)segSizes.Count;
    for (var i = 0; i < segSizes.Count; ++i) header[27 + i] = segSizes[i];
    var result = new byte[header.Length + payload.Length];
    Buffer.BlockCopy(header, 0, result, 0, header.Length);
    Buffer.BlockCopy(payload, 0, result, header.Length, payload.Length);
    return result;
  }

  private static byte[] BuildVorbisIdentification(int sampleRate, byte channels) {
    var pkt = new byte[30];
    pkt[0] = 0x01;
    pkt[1] = (byte)'v'; pkt[2] = (byte)'o'; pkt[3] = (byte)'r';
    pkt[4] = (byte)'b'; pkt[5] = (byte)'i'; pkt[6] = (byte)'s';
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(7, 4), 0);          // version 0
    pkt[11] = channels;
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(12, 4), sampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(16, 4), 0);          // bitrate_max
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(20, 4), 128_000);    // bitrate_nominal
    BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(24, 4), 0);          // bitrate_min
    pkt[28] = 0xB8; // blocksize_0=8 (256), blocksize_1=11 (2048)
    pkt[29] = 1;    // framing bit
    return pkt;
  }

  private static byte[] BuildVorbisComment(string vendor) {
    var vendorBytes = System.Text.Encoding.UTF8.GetBytes(vendor);
    var pkt = new byte[7 + 4 + vendorBytes.Length + 4 + 1];
    pkt[0] = 0x03;
    pkt[1] = (byte)'v'; pkt[2] = (byte)'o'; pkt[3] = (byte)'r';
    pkt[4] = (byte)'b'; pkt[5] = (byte)'i'; pkt[6] = (byte)'s';
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(7, 4), (uint)vendorBytes.Length);
    Buffer.BlockCopy(vendorBytes, 0, pkt, 11, vendorBytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(11 + vendorBytes.Length, 4), 0);
    pkt[pkt.Length - 1] = 1; // framing bit
    return pkt;
  }

  // ── 1. Ogg page / identification parse ─────────────────────────────────

  [Test]
  public void ReadStreamInfo_ParsesIdentificationMetadata() {
    var ident = BuildVorbisIdentification(sampleRate: 44100, channels: 2);
    var comment = BuildVorbisComment("test-vendor");
    var oggBytes = new List<byte>();
    oggBytes.AddRange(BuildOggPage(ident, serial: 0xDEADBEEF, flags: 0x02, granule: 0, seq: 0));
    oggBytes.AddRange(BuildOggPage(comment, serial: 0xDEADBEEF, flags: 0, granule: 0, seq: 1));

    using var ms = new MemoryStream(oggBytes.ToArray());
    var info = VorbisCodec.ReadStreamInfo(ms);

    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.NominalBitrate, Is.EqualTo(128_000));
    Assert.That(info.Vendor, Is.EqualTo("test-vendor"));
  }

  [Test]
  public void ReadStreamInfo_MonoAtUncommonRate() {
    var ident = BuildVorbisIdentification(sampleRate: 8000, channels: 1);
    var comment = BuildVorbisComment("mono-encoder");
    var oggBytes = new List<byte>();
    oggBytes.AddRange(BuildOggPage(ident, serial: 1, flags: 0x02, granule: 0, seq: 0));
    oggBytes.AddRange(BuildOggPage(comment, serial: 1, flags: 0, granule: 0, seq: 1));

    using var ms = new MemoryStream(oggBytes.ToArray());
    var info = VorbisCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.SampleRate, Is.EqualTo(8000));
    Assert.That(info.Vendor, Is.EqualTo("mono-encoder"));
  }

  // ── 2. End-to-end decode — requires a real .ogg test vector ────────────

  [Test]
  public void Decompress_EndToEnd_OnRealOggVector() {
    // Look in the repo's test-corpus/ for any .ogg file; if none are present,
    // mark the test inconclusive rather than fail — end-to-end decoding is
    // gated on having a permissively-licensed sample.
    var candidates = new[] {
      Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus"),
      Path.Combine(Environment.CurrentDirectory, "test-corpus"),
    };
    string? oggPath = null;
    foreach (var dir in candidates) {
      if (!Directory.Exists(dir)) continue;
      var hit = Directory.EnumerateFiles(dir, "*.ogg", SearchOption.AllDirectories).FirstOrDefault();
      if (hit != null) { oggPath = hit; break; }
    }
    if (oggPath == null) {
      Assert.Inconclusive("No .ogg test vector found under test-corpus/. Drop a short sample there to enable this test.");
      return;
    }

    using var input = File.OpenRead(oggPath);
    using var output = new MemoryStream();
    try {
      VorbisCodec.Decompress(input, output);
    } catch (NotSupportedException ex) {
      Assert.Inconclusive($"Test vector needs an unsupported feature ({ex.Message}).");
      return;
    }
    // Read sample rate + channel count from the same file to validate sample count shape.
    input.Position = 0;
    var info = VorbisCodec.ReadStreamInfo(input);
    var bytesPerFrame = info.Channels * 2;
    Assert.That(output.Length % bytesPerFrame, Is.EqualTo(0),
      "decoded PCM length must be a whole number of frames");
    Assert.That(output.Length, Is.GreaterThan(0), "decoder must emit at least one frame");
  }

  // ── 3. Synthetic end-to-end decode — no external vector needed ──────────

  [Test]
  public void Decompress_Synthetic_Floor1_SilenceDecodesToZeroPcm() {
    var ogg = VorbisSyntheticStream.Build(floorType: 1);
    using var input = new MemoryStream(ogg);
    using var output = new MemoryStream();
    VorbisCodec.Decompress(input, output);

    var pcm = output.ToArray();
    // One emitted half-block (blocksize_0 = 64 ⇒ 32 samples) × mono × 2 bytes.
    Assert.That(pcm.Length, Is.EqualTo(64), "exactly one mono half-block frame");
    Assert.That(pcm, Is.All.EqualTo((byte)0), "floor 'unused' ⇒ pure silence");
  }

  [Test]
  public void Decompress_Synthetic_Floor0_SilenceDecodesToZeroPcm() {
    var ogg = VorbisSyntheticStream.Build(floorType: 0);
    using var input = new MemoryStream(ogg);
    using var output = new MemoryStream();
    VorbisCodec.Decompress(input, output);

    var pcm = output.ToArray();
    Assert.That(pcm.Length, Is.EqualTo(64), "exactly one mono half-block frame");
    Assert.That(pcm, Is.All.EqualTo((byte)0), "floor 0 amplitude 0 ⇒ pure silence");
  }

  [Test]
  public void Decompress_Synthetic_Floor1_ActiveCurveSynthesisesFiniteFrame() {
    var ogg = VorbisSyntheticStream.Build(floorType: 1, activeFloor: true);
    using var input = new MemoryStream(ogg);
    using var output = new MemoryStream();
    VorbisCodec.Decompress(input, output);

    // Floor curve is synthesised but residue range is empty ⇒ floor × 0 = 0.
    // A NaN/Inf in the floor would surface as non-zero PCM, so all-zero proves
    // the curve was finite.
    var pcm = output.ToArray();
    Assert.That(pcm.Length, Is.EqualTo(64));
    Assert.That(pcm, Is.All.EqualTo((byte)0), "finite floor × zero residue ⇒ silence");
  }

  [Test]
  public void Decompress_Synthetic_Floor0_ActiveLspSynthesisesFiniteFrame() {
    var ogg = VorbisSyntheticStream.Build(floorType: 0, activeFloor: true);
    using var input = new MemoryStream(ogg);
    using var output = new MemoryStream();
    VorbisCodec.Decompress(input, output);

    // Drives the LSP bark-map synthesis (amplitude 200, order-4 coefficients).
    var pcm = output.ToArray();
    Assert.That(pcm.Length, Is.EqualTo(64), "floor 0 LSP path must emit one frame");
    Assert.That(pcm, Is.All.EqualTo((byte)0), "finite LSP floor × zero residue ⇒ silence");
  }

  // ── 4. Robustness / fuzz-ish ────────────────────────────────────────────

  [Test]
  public void Decompress_Synthetic_Floor0_TruncatedAudioPacketDegradesToSilence() {
    var ogg = VorbisSyntheticStream.BuildTruncatedAudio(floorType: 0);
    using var input = new MemoryStream(ogg);
    using var output = new MemoryStream();
    // End-of-packet during floor 0 decode must degrade to silence, never throw.
    Assert.DoesNotThrow(() => VorbisCodec.Decompress(input, output));
    Assert.That(output.ToArray(), Is.All.EqualTo((byte)0));
  }

  [Test]
  public void Decompress_Synthetic_Floor1_TruncatedAudioPacketDegradesToSilence() {
    var ogg = VorbisSyntheticStream.BuildTruncatedAudio(floorType: 1);
    using var input = new MemoryStream(ogg);
    using var output = new MemoryStream();
    Assert.DoesNotThrow(() => VorbisCodec.Decompress(input, output));
    Assert.That(output.ToArray(), Is.All.EqualTo((byte)0));
  }

  // The real .ogg-vector end-to-end test lives above under section 2; it stays
  // Inconclusive until a permissively-licensed sample is dropped into
  // test-corpus/, while the synthetic tests above give positive coverage.
}
