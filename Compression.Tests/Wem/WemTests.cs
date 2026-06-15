#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.WwiseIma;
using FileFormat.Wem;

namespace Compression.Tests.Wem;

[TestFixture]
public class WemTests {

  // Builds a minimal RIFF/WAVE WEM around a fmt body + data + optional extra chunks.
  private static byte[] BuildWem(byte[] fmtBody, byte[] data,
      IEnumerable<(string Id, byte[] Body)>? extras = null) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);

    void Chunk(string id, byte[] body) {
      w.Write(Encoding.ASCII.GetBytes(id));
      w.Write((uint)body.Length);
      w.Write(body);
      if ((body.Length & 1) != 0) w.Write((byte)0); // pad
    }

    // Reserve RIFF header; patch size at the end.
    w.Write("RIFF"u8.ToArray());
    w.Write(0u);
    w.Write("WAVE"u8.ToArray());
    Chunk("fmt ", fmtBody);
    Chunk("data", data);
    if (extras != null)
      foreach (var (id, body) in extras)
        Chunk(id, body);

    var bytes = ms.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
    return bytes;
  }

  private static byte[] FmtBody(int tag, int channels, int rate, int blockAlign, int bits, ushort cbSize = 0) {
    var body = new byte[cbSize > 0 ? 18 + cbSize : 16];
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), (ushort)tag);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)rate);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)(rate * blockAlign));
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), (ushort)blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)bits);
    if (cbSize > 0)
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(16), cbSize);
    return body;
  }

  // ──────────── Wwise-IMA payload → channels appear ────────────

  [Test]
  public void WwiseIma_Stereo_SurfacesDecodedChannels() {
    const int blockAlign = 0x48; // stereo, per-channel data 32 bytes
    var samplesPerBlock = (blockAlign / 2 - 4) * 2 + 1;
    var frames = samplesPerBlock * 3;
    var pcm = new short[frames * 2];
    for (var f = 0; f < frames; ++f) {
      pcm[f * 2] = (short)(Math.Sin(f / 5.0) * 8000);
      pcm[f * 2 + 1] = (short)(Math.Cos(f / 6.0) * 7000);
    }
    var coded = WwiseImaCodec.Encode(pcm, channels: 2, blockAlign: blockAlign);

    var wem = BuildWem(FmtBody(0x0002, 2, 32000, blockAlign, 16), coded);

    using var ms = new MemoryStream(wem);
    var entries = new WemFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.wem"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  // ──────────── PCM payload → channels appear ────────────

  [Test]
  public void Pcm_Stereo_SplitsIntoChannels() {
    var frames = 64;
    var data = new byte[frames * 2 * 2]; // stereo 16-bit
    for (var f = 0; f < frames; ++f) {
      BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((f * 2) * 2), (short)(f * 100));
      BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((f * 2 + 1) * 2), (short)(-f * 50));
    }
    var wem = BuildWem(FmtBody(0x0001, 2, 44100, 4, 16), data);

    using var ms = new MemoryStream(wem);
    var entries = new WemFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
  }

  // ──────────── Wwise-Vorbis tag → graceful FULL-only ────────────

  [Test]
  public void WwiseVorbis_Tag_FallsBackToFullOnly() {
    var wem = BuildWem(FmtBody(0xFFFF, 2, 48000, 0, 0), [0xDE, 0xAD, 0xBE, 0xEF]);

    using var ms = new MemoryStream(wem);
    var entries = new WemFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.wem"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);

    using var metaOut = new MemoryStream();
    using var ms2 = new MemoryStream(wem);
    new WemFormatDescriptor().ExtractEntry(ms2, "metadata.ini", metaOut, null);
    var meta = Encoding.UTF8.GetString(metaOut.ToArray());
    Assert.That(meta, Does.Contain("0xFFFF"));
    Assert.That(meta, Does.Contain("Vorbis"));
  }

  // ──────────── Auxiliary chunks → metadata/*.bin ────────────

  [Test]
  public void ExtraChunks_AreSurfacedAsMetadataBins() {
    var wem = BuildWem(
      FmtBody(0x0001, 1, 22050, 2, 16),
      new byte[8],
      [("akd ", [1, 2, 3, 4]), ("cue ", [9, 9])]);

    using var ms = new MemoryStream(wem);
    var entries = new WemFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "metadata/akd.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata/cue.bin"), Is.True);

    using var extract = new MemoryStream();
    using var ms2 = new MemoryStream(wem);
    new WemFormatDescriptor().ExtractEntry(ms2, "metadata/akd.bin", extract, null);
    Assert.That(extract.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
  }
}
