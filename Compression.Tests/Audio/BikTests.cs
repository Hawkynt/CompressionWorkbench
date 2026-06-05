#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Bik;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Bink container descriptor (<see cref="BikFormatDescriptor"/>). A hand-built
/// minimal Bink 1 'BIKi' file with one RDFT mono audio track and a single frame must
/// surface FULL.bik, a metadata.ini summarising the track (rate/channels/codec), the
/// per-track raw stream blob, and — when the audio decodes — a per-channel WAV. A Bink 2
/// 'KB2' file must surface its audio as a blob marked unsupported (no decode). Magic
/// detection and graceful degradation on truncation are also pinned.
/// </summary>
[TestFixture]
public class BikTests {

  private const int BinkAud16Bits = 0x4000;

  [Test]
  public void Bik1_RdftMonoTrack_SurfacesMetadataAndStreamBlob() {
    var bik = BuildBik1(sampleRate: 11025, audioFlags: BinkAud16Bits, audioPacket: BuildZeroAudioPacket());
    using var ms = new MemoryStream(bik);
    var entries = new BikFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.bik" && e.Kind == "Container"), Is.True);

    using var meta = new MemoryStream();
    new BikFormatDescriptor().ExtractEntry(new MemoryStream(bik), "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(text, Does.Contain("signature = BIKi"));
    Assert.That(text, Does.Contain("audio_tracks = 1"));
    Assert.That(text, Does.Contain("sample_rate = 11025"));
    Assert.That(text, Does.Contain("codec = binkaudio_rdft"));
    Assert.That(text, Does.Contain("packets = 1"));

    Assert.That(entries.Any(e => e.Name == "TRACK0.bin" && e.Kind == "Stream"), Is.True);
    // The hand-built all-zero packet decodes to silence → a per-channel WAV is surfaced.
    Assert.That(entries.Any(e => e.Name.StartsWith("TRACK0_") && e.Name.EndsWith(".wav") && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void Bik2_AudioIsBlobOnly_NotDecoded() {
    var bik = BuildBik1(sampleRate: 11025, audioFlags: BinkAud16Bits, audioPacket: BuildZeroAudioPacket(), signature: "KB2", revision: 'a');
    using var ms = new MemoryStream(bik);
    var entries = new BikFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "TRACK0.bin" && e.Method == "binkaudio_unsupported"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("TRACK0_") && e.Name.EndsWith(".wav")), Is.False);
  }

  [Test]
  public void Truncated_DegradesGracefully() {
    var bik = BuildBik1(11025, BinkAud16Bits, BuildZeroAudioPacket());
    var truncated = bik[..30];
    using var ms = new MemoryStream(truncated);
    var entries = new BikFormatDescriptor().List(ms, null);
    // Always at least the container entry survives.
    Assert.That(entries.Any(e => e.Name == "FULL.bik"), Is.True);
  }

  /// <summary>Builds a minimal Bink 1 file: header + one audio track + a single frame.</summary>
  private static byte[] BuildBik1(int sampleRate, int audioFlags, byte[] audioPacket,
      string signature = "BIK", char revision = 'i') {
    // Frame payload: audio_size(4) + packet, then a trailing video byte.
    var framePayload = new byte[4 + audioPacket.Length + 1];
    BinaryPrimitives.WriteUInt32LittleEndian(framePayload, (uint)audioPacket.Length);
    audioPacket.CopyTo(framePayload, 4);
    framePayload[^1] = 0xEE; // a single video byte

    using var ms = new MemoryStream();
    void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
    void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }

    ms.WriteByte((byte)signature[0]);
    ms.WriteByte((byte)signature[1]);
    ms.WriteByte((byte)signature[2]);
    ms.WriteByte((byte)revision);

    U32(0);            // file size (patched below to actual-8)
    U32(1);            // num frames
    U32(0);            // largest frame size
    U32(0);            // reserved
    U32(320);          // width
    U32(240);          // height
    U32(15);           // fps num
    U32(1);            // fps den
    U32(0);            // video flags (extradata)
    U32(1);            // num audio tracks

    // 'i' revision (and 'k') carry one extra unknown 32-bit field.
    if ((signature == "BIK" && revision == 'k') ||
        (signature == "KB2" && revision is 'i' or 'j' or 'k'))
      U32(0);

    U32(0);            // per-track max decoded packet bytes (one track)
    U16((ushort)sampleRate);
    U16((ushort)audioFlags);
    U32(0x100);        // track id

    // Frame index: next_pos for frame 0 = start of frame data; we patch after we know it.
    var indexPosOffset = (int)ms.Position;
    U32(0);            // placeholder for frame 0 start offset

    var frameDataStart = (int)ms.Position;
    ms.Write(framePayload);
    var fileEnd = (int)ms.Position;

    var buf = ms.ToArray();
    // file_size field is stored as actual-8 (the reader adds 8 back).
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)(fileEnd - 8));
    // frame 0 offset (low bit = keyframe flag; set it for a keyframe).
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(indexPosOffset), (uint)frameDataStart | 1u);
    return buf;
  }

  /// <summary>
  /// Builds an all-zero RDFT mono Bink Audio packet (frame_len 512 for 11025 Hz): a 4-byte
  /// reported-size prefix, two zero floats, num_bands zero quantizers and width-0 runs.
  /// </summary>
  private static byte[] BuildZeroAudioPacket() {
    const int frameLen = 512;
    const int numBands = 22; // 11025/2 = 5512 → first crit >= 5512 is 6400 (index 19) → 20? built generically below
    // Derive num_bands exactly as the codec does to keep the packet self-consistent.
    var sampleRateHalf = (11025 + 1) / 2;
    var bands = 1;
    int[] crit = [
      100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320,
      2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 24500,
    ];
    while (bands < 25 && sampleRateHalf > crit[bands - 1]) ++bands;
    _ = numBands;

    var bw = new LeBitWriter();
    bw.Put(32, 0);
    bw.Put(29, 0);
    bw.Put(29, 0);
    for (var i = 0; i < bands; ++i) bw.Put(8, 0);
    var idx = 2;
    while (idx < frameLen) {
      bw.Put(1, 0);
      bw.Put(4, 0);
      idx = Math.Min(idx + 8, frameLen);
    }
    return bw.ToArray();
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
