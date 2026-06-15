#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Tests.Codecs.Vorbis;
using FileFormat.Matroska;

namespace Compression.Tests.Mkv;

/// <summary>
/// End-to-end Matroska Vorbis reconstruction: a real (synthetic) Ogg-Vorbis stream is
/// disassembled into its three setup headers (xiph-laced into <c>CodecPrivate</c>) plus an
/// audio packet (one SimpleBlock). The descriptor must rebuild a decodable Ogg stream from
/// these and surface a decoded mono channel — proving the synthetic-Ogg round-trip works
/// against the repository's actual Vorbis decoder.
/// </summary>
[TestFixture]
public class MkvVorbisEndToEndTests {

  /// <summary>Reassembles all packets of the first logical bitstream from an Ogg byte buffer.</summary>
  private static List<byte[]> ReadOggPackets(byte[] ogg) {
    var packets = new List<byte[]>();
    var current = new List<byte>();
    var pos = 0;
    while (pos + 27 <= ogg.Length) {
      var segCount = ogg[pos + 26];
      var segTable = pos + 27;
      var payload = segTable + segCount;
      for (var i = 0; i < segCount; ++i) {
        var segLen = ogg[segTable + i];
        current.AddRange(ogg.AsSpan(payload, segLen).ToArray());
        payload += segLen;
        if (segLen < 255) { packets.Add(current.ToArray()); current = []; }
      }
      pos = payload;
    }
    return packets;
  }

  /// <summary>Builds a Vorbis CodecPrivate (xiph-laced 3 headers) from the three setup packets.</summary>
  private static byte[] BuildVorbisCodecPrivate(byte[] ident, byte[] comment, byte[] setup) {
    var ms = new MemoryStream();
    ms.WriteByte(2); // header count - 1
    WriteXiphLength(ms, ident.Length);
    WriteXiphLength(ms, comment.Length);
    ms.Write(ident); ms.Write(comment); ms.Write(setup);
    return ms.ToArray();
  }

  private static void WriteXiphLength(MemoryStream ms, int length) {
    while (length >= 255) { ms.WriteByte(255); length -= 255; }
    ms.WriteByte((byte)length);
  }

  // ── MKV writer helpers ─────────────────────────────────────────────────────────

  private static void WriteId(MemoryStream ms, ulong id) {
    if (id <= 0xFF) ms.WriteByte((byte)id);
    else if (id <= 0xFFFF) { ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id); }
    else if (id <= 0xFFFFFF) { ms.WriteByte((byte)(id >> 16)); ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id); }
    else { ms.WriteByte((byte)(id >> 24)); ms.WriteByte((byte)(id >> 16)); ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id); }
  }

  private static void WriteSize(MemoryStream ms, int size) {
    if (size <= 127) ms.WriteByte((byte)(0x80 | size));
    else if (size <= 16383) { ms.WriteByte((byte)(0x40 | (size >> 8))); ms.WriteByte((byte)size); }
    else if (size <= 0x1FFFFF) { ms.WriteByte((byte)(0x20 | (size >> 16))); ms.WriteByte((byte)(size >> 8)); ms.WriteByte((byte)size); }
    else { ms.WriteByte((byte)(0x10 | (size >> 24))); ms.WriteByte((byte)(size >> 16)); ms.WriteByte((byte)(size >> 8)); ms.WriteByte((byte)size); }
  }

  private static void WriteElement(MemoryStream ms, ulong id, byte[] body) {
    WriteId(ms, id); WriteSize(ms, body.Length); ms.Write(body);
  }

  private static byte[] Element(ulong id, byte[] body) {
    var ms = new MemoryStream(); WriteElement(ms, id, body); return ms.ToArray();
  }

  private static byte[] MakeVorbisMkv(byte[] codecPrivate, IReadOnlyList<byte[]> audioPackets, int channels, double rate) {
    var ms = new MemoryStream();

    // EBML header
    var hdr = new MemoryStream();
    hdr.Write(new byte[] { 0x42, 0x86, 0x81, 0x01 });
    hdr.WriteByte(0x42); hdr.WriteByte(0x82); hdr.WriteByte(0x88); hdr.Write("matroska"u8);
    WriteElement(ms, 0x1A45DFA3, hdr.ToArray());

    WriteId(ms, 0x18538067);
    ms.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    // Tracks → one audio TrackEntry
    var entry = new MemoryStream();
    WriteElement(entry, 0xD7, [1]);                       // TrackNumber
    WriteElement(entry, 0x83, [2]);                       // TrackType audio
    WriteElement(entry, 0x86, Encoding.UTF8.GetBytes("A_VORBIS"));
    WriteElement(entry, 0x63A2, codecPrivate);            // CodecPrivate
    var audio = new MemoryStream();
    var freq = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(freq, rate);
    WriteElement(audio, 0xB5, freq);
    WriteElement(audio, 0x9F, [(byte)channels]);
    WriteElement(entry, 0xE1, audio.ToArray());
    WriteElement(ms, 0x1654AE6B, Element(0xAE, entry.ToArray()));

    // Cluster → one SimpleBlock per audio packet (no lacing).
    var cluster = new MemoryStream();
    WriteElement(cluster, 0xE7, [0x00]);
    foreach (var packet in audioPackets) {
      var block = new byte[4 + packet.Length];
      block[0] = 0x81; // track 1
      packet.CopyTo(block, 4);
      WriteElement(cluster, 0xA3, block);
    }
    WriteElement(ms, 0x1F43B675, cluster.ToArray());

    return ms.ToArray();
  }

  [Test]
  public void VorbisInMkv_DecodesToMonoChannel() {
    // A real synthetic Ogg-Vorbis stream (mono, 8 kHz). Disassemble it into the 3 setup
    // headers + audio packet, repackage as MKV, and verify the descriptor reconstructs a
    // decodable Ogg stream → one MONO channel.
    var ogg = VorbisSyntheticStream.Build(floorType: 1, activeFloor: false, sampleRate: 8000, channels: 1);
    var packets = ReadOggPackets(ogg);
    Assert.That(packets.Count, Is.GreaterThanOrEqualTo(4), "expected ident+comment+setup+audio packets");

    var codecPrivate = BuildVorbisCodecPrivate(packets[0], packets[1], packets[2]);
    var audioPackets = packets.Skip(3).ToList(); // every packet after the 3 setup headers
    var mkv = MakeVorbisMkv(codecPrivate, audioPackets, channels: 1, rate: 8000);

    using var ms = new MemoryStream(mkv);
    var entries = new MkvFormatDescriptor().List(ms, null);
    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Count, Is.EqualTo(1));
    Assert.That(channels[0].Name, Is.EqualTo("TRACK0_MONO.wav"));
  }
}
