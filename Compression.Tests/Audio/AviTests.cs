#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Avi;

namespace Compression.Tests.Audio;

[TestFixture]
public class AviTests {

  /// <summary>Synthesises a tiny AVI with one video track + one audio track.</summary>
  private static byte[] MakeMinimalAvi() {
    // ── avih (56 bytes) ────────────────────────────────────────
    var avih = new byte[56];
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(0), 33333);    // microseconds per frame
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(16), 2);        // total frames
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(24), 2);        // streams
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(32), 320);      // width
    BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(36), 240);      // height

    // ── Video strh (56 bytes) ─────────────────────────────────
    var vidStrh = new byte[56];
    "vids"u8.CopyTo(vidStrh.AsSpan(0));
    "MJPG"u8.CopyTo(vidStrh.AsSpan(4));

    // ── Video strf = BITMAPINFOHEADER (40 bytes) ──────────────
    var vidStrf = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(0), 40);        // biSize
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(4), 320);       // biWidth
    BinaryPrimitives.WriteUInt32LittleEndian(vidStrf.AsSpan(8), 240);       // biHeight
    BinaryPrimitives.WriteUInt16LittleEndian(vidStrf.AsSpan(12), 1);        // planes
    BinaryPrimitives.WriteUInt16LittleEndian(vidStrf.AsSpan(14), 24);       // bitcount
    "MJPG"u8.CopyTo(vidStrf.AsSpan(16));                                    // compression

    // ── Audio strh (56 bytes) ─────────────────────────────────
    var audStrh = new byte[56];
    "auds"u8.CopyTo(audStrh.AsSpan(0));

    // ── Audio strf = WAVEFORMATEX (16 bytes) ──────────────────
    var audStrf = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(audStrf.AsSpan(0), 1);         // wFormatTag = PCM
    BinaryPrimitives.WriteUInt16LittleEndian(audStrf.AsSpan(2), 1);         // channels
    BinaryPrimitives.WriteUInt32LittleEndian(audStrf.AsSpan(4), 22050);     // sample rate
    BinaryPrimitives.WriteUInt32LittleEndian(audStrf.AsSpan(8), 44100);     // bytes/sec
    BinaryPrimitives.WriteUInt16LittleEndian(audStrf.AsSpan(12), 2);        // block align
    BinaryPrimitives.WriteUInt16LittleEndian(audStrf.AsSpan(14), 16);       // bits/sample

    // ── Build strl lists ──────────────────────────────────────
    var vidStrl = BuildList("strl",
      BuildChunk("strh", vidStrh),
      BuildChunk("strf", vidStrf));
    var audStrl = BuildList("strl",
      BuildChunk("strh", audStrh),
      BuildChunk("strf", audStrf));

    // ── hdrl list ─────────────────────────────────────────────
    var hdrl = BuildList("hdrl",
      BuildChunk("avih", avih),
      vidStrl,
      audStrl);

    // ── movi list with two sample chunks ──────────────────────
    var frameBytes = Encoding.ASCII.GetBytes("FRAME-DATA!");
    var audioBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
    var movi = BuildList("movi",
      BuildChunk("00dc", frameBytes),
      BuildChunk("01wb", audioBytes));

    // ── RIFF wrap ─────────────────────────────────────────────
    using var mem = new MemoryStream();
    mem.Write("RIFF"u8);
    var inner = new byte[4 + hdrl.Length + movi.Length];
    "AVI "u8.CopyTo(inner.AsSpan(0));
    hdrl.CopyTo(inner.AsSpan(4));
    movi.CopyTo(inner.AsSpan(4 + hdrl.Length));
    var sizeBytes = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)inner.Length);
    mem.Write(sizeBytes);
    mem.Write(inner);
    return mem.ToArray();
  }

  private static byte[] BuildChunk(string id, byte[] body) {
    var sizeAligned = body.Length + (body.Length & 1);
    var chunk = new byte[8 + sizeAligned];
    Encoding.ASCII.GetBytes(id).CopyTo(chunk.AsSpan(0));
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)body.Length);
    body.CopyTo(chunk.AsSpan(8));
    return chunk;
  }

  private static byte[] BuildList(string listType, params byte[][] children) {
    var bodyLen = 4 /* LIST type */ + children.Sum(c => c.Length);
    var chunk = new byte[8 + bodyLen];
    "LIST"u8.CopyTo(chunk.AsSpan(0));
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)bodyLen);
    Encoding.ASCII.GetBytes(listType).CopyTo(chunk.AsSpan(8));
    var off = 12;
    foreach (var c in children) { c.CopyTo(chunk.AsSpan(off)); off += c.Length; }
    return chunk;
  }

  [Test]
  public void AviReader_ParsesAvihAndTracks() {
    var blob = MakeMinimalAvi();
    var parsed = new AviReader().Read(blob);
    Assert.That(parsed.Width, Is.EqualTo(320));
    Assert.That(parsed.Height, Is.EqualTo(240));
    Assert.That(parsed.Tracks.Count, Is.EqualTo(2));
    Assert.That(parsed.Tracks[0].StreamType, Is.EqualTo("vids"));
    Assert.That(parsed.Tracks[1].StreamType, Is.EqualTo("auds"));
    Assert.That(parsed.Tracks[1].AudioChannels, Is.EqualTo(1));
    Assert.That(parsed.Tracks[1].AudioSampleRate, Is.EqualTo(22050));
  }

  [Test]
  public void AviReader_CollectsMoviChunks() {
    var blob = MakeMinimalAvi();
    var parsed = new AviReader().Read(blob);
    Assert.That(parsed.Tracks[0].Data.Length, Is.GreaterThan(0));
    Assert.That(Encoding.ASCII.GetString(parsed.Tracks[0].Data), Is.EqualTo("FRAME-DATA!"));
    Assert.That(parsed.Tracks[1].Data, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
  }

  [Test]
  public void AviDescriptor_EnumeratesTracksAndMetadata() {
    var blob = MakeMinimalAvi();
    using var ms = new MemoryStream(blob);
    var entries = new AviFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.avi"), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("track_00_video")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("track_01_audio")), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void AviDescriptor_PcmAudioWrappedInWav() {
    var blob = MakeMinimalAvi();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AviFormatDescriptor().ExtractEntry(ms, "track_01_audio.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
  }
}
