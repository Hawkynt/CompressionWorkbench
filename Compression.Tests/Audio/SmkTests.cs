#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Smk;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Smacker container descriptor (<see cref="SmkFormatDescriptor"/>). A hand-built
/// minimal 'SMK4' file with one compressed-audio (SMKA) mono track and a single frame
/// carrying one audio chunk must surface FULL.smk, a metadata.ini summarising the track
/// (rate/channels/codec), the per-track raw stream blob and a decoded per-channel WAV.
/// Magic detection and graceful degradation on truncation are also pinned.
/// </summary>
[TestFixture]
public class SmkTests {

  private const int SmkAudPacked = 0x80; // compressed Smacker audio

  [Test]
  public void Smk_SmkaMonoTrack_SurfacesMetadataStreamAndChannel() {
    var smk = BuildSmk(sampleRate: 22050, aflag: SmkAudPacked, audioChunkPayload: BuildSmka8BitMono());
    using var ms = new MemoryStream(smk);
    var entries = new SmkFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.smk" && e.Kind == "Container"), Is.True);

    using var meta = new MemoryStream();
    new SmkFormatDescriptor().ExtractEntry(new MemoryStream(smk), "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(text, Does.Contain("magic = SMK4"));
    Assert.That(text, Does.Contain("sample_rate = 22050"));
    Assert.That(text, Does.Contain("codec = smackaud"));
    Assert.That(text, Does.Contain("chunks = 1"));

    Assert.That(entries.Any(e => e.Name == "TRACK0.bin" && e.Kind == "Stream"), Is.True);
    Assert.That(entries.Any(e => e.Name == "TRACK0_MONO.wav" && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void Truncated_DegradesGracefully() {
    var smk = BuildSmk(22050, SmkAudPacked, BuildSmka8BitMono());
    using var ms = new MemoryStream(smk[..40]);
    var entries = new SmkFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.smk"), Is.True);
  }

  /// <summary>Builds a minimal SMK4 with one audio track and a single frame.</summary>
  private static byte[] BuildSmk(int sampleRate, int aflag, byte[] audioChunkPayload) {
    // Frame data: one audio chunk = u32 size (incl. the 4-byte length) + payload. The
    // frame size table stores sizes masked to a multiple of 4 (Smacker invariant), so pad
    // the frame data up to a 4-byte boundary with trailing video bytes.
    var chunkSize = 4 + audioChunkPayload.Length;
    var paddedLen = (chunkSize + 3) & ~3;
    var frameData = new byte[paddedLen];
    BinaryPrimitives.WriteUInt32LittleEndian(frameData, (uint)chunkSize);
    audioChunkPayload.CopyTo(frameData, 4);

    using var ms = new MemoryStream();
    void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }

    ms.Write("SMK4"u8);
    U32(8);   // width
    U32(8);   // height
    U32(1);   // frames
    U32(100); // pts_inc
    U32(0);   // flags (no ring frame)

    for (var i = 0; i < 28; ++i) ms.WriteByte(0); // skipped audio-size data

    U32(0);   // tree size (no video trees)

    for (var i = 0; i < 16; ++i) ms.WriteByte(0); // mmap/mclr/full/type sizes

    // 7 audio track descriptors: u24 rate + u8 flags. Track 0 present, rest silent.
    for (var i = 0; i < 7; ++i) {
      if (i == 0) {
        ms.WriteByte((byte)(sampleRate & 0xFF));
        ms.WriteByte((byte)((sampleRate >> 8) & 0xFF));
        ms.WriteByte((byte)((sampleRate >> 16) & 0xFF));
        ms.WriteByte((byte)aflag);
      } else {
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
      }
    }

    U32(0); // padding

    // Frame-size table (1 frame): size = frame data length (already a multiple of 4 here).
    U32((uint)frameData.Length);
    // Frame-flags table (1 byte): bit 1 set → audio track 0 present this frame.
    ms.WriteByte(0x02);

    // No tree blob (treesize == 0); frame data follows immediately.
    ms.Write(frameData);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds an 8-bit mono SMKA chunk payload (its own 4-byte unpacked-size prefix + the
  /// LSB-first bitstream): a two-symbol tree (node, leaf 2, leaf 200) and one delta.
  /// 4 unpacked samples: base 10 then deltas A(2), B(200), A(2) → 10,12,212,214.
  /// </summary>
  private static byte[] BuildSmka8BitMono() {
    var body = new LeBitWriter();
    body.Put(1, 1); // data present
    body.Put(1, 0); // mono
    body.Put(1, 0); // 8-bit
    body.Put(1, 0); // tree skip-bit
    body.Put(1, 1); //   node
    body.Put(1, 0); //   leaf
    body.Put(8, 2); //     value 2
    body.Put(1, 0); //   leaf
    body.Put(8, 200);//    value 200
    body.Put(1, 0); // tree skip-bit
    body.Put(8, 10);// base sample
    body.Put(1, 0); // delta A
    body.Put(1, 1); // delta B
    body.Put(1, 0); // delta A
    var bits = body.ToArray();

    var payload = new byte[4 + bits.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(payload, 4); // unpacked size = 4 samples
    bits.CopyTo(payload, 4);
    return payload;
  }

  private sealed class LeBitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bit;
    public void Put(int n, uint value) {
      for (var i = 0; i < n; ++i) {
        var b = (int)((value >> i) & 1);
        this._cur |= b << this._bit;
        if (++this._bit == 8) { this._bytes.Add((byte)this._cur); this._cur = 0; this._bit = 0; }
      }
    }
    public byte[] ToArray() {
      if (this._bit != 0) { this._bytes.Add((byte)this._cur); this._cur = 0; this._bit = 0; }
      return this._bytes.ToArray();
    }
  }
}
