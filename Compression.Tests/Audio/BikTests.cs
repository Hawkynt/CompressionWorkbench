#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Bik;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins Bink packet-aware demux and packet-preserving mux/remux. Hand-built Bink 1/2 files
/// keep the tests independent of proprietary encoders while exercising the public container
/// framing: headers, audio packet prefixes, frame offsets, keyframe bits and elementary-stream
/// extraction.
/// </summary>
[TestFixture]
public class BikTests {

  private const int BinkAud16Bits = 0x4000;

  [Test]
  public void Descriptor_AdvertisesMuxCreation() {
    var descriptor = new BikFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveWriteConstraints>());
  }

  [Test]
  public void Descriptor_WriteConstraints_AcceptsOnlyCanonicalRootComponents() {
    var descriptor = new BikFormatDescriptor();

    foreach (var name in new[] { "FULL.bik", "metadata.ini", "VIDEO.bin", "TRACK0.bin", "track123.bin" })
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory(name, []), out var reason), Is.True,
        $"{name}: {reason}");

    foreach (var name in new[] { "anything.bin", "TRACK.bin", "TRACK-1.bin", "TRACK0.wav", "nested/VIDEO.bin" })
      Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory(name, []), out _), Is.False, name);

    var directory = new ArchiveInputInfo("metadata.ini", "metadata.ini", IsDirectory: true);
    Assert.That(descriptor.CanAccept(directory, out _), Is.False);
  }

  [Test]
  public void Create_UnsupportedInputName_IsArgumentException() {
    var descriptor = new BikFormatDescriptor();
    using var output = new MemoryStream();

    Assert.That(() => descriptor.Create(output, [
      ArchiveInputInfo.InMemory("arbitrary.bin", "BIKi"u8.ToArray()),
    ], new FormatCreateOptions()), Throws.TypeOf<ArgumentException>());
  }

  [Test]
  public void Mux_MalformedNumericMetadata_IsInvalidDataException() {
    const string metadata = """
      [Bink]
      signature = BIKi
      frames = nope
      width = 320
      height = 200
      fps_num = 25
      fps_den = 1
      audio_tracks = 0
      """;
    var descriptor = new BikFormatDescriptor();
    using var output = new MemoryStream();

    Assert.That(() => descriptor.Create(output, [
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes(metadata)),
      ArchiveInputInfo.InMemory("VIDEO.bin", []),
    ], new FormatCreateOptions()), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Bik1_RdftMonoTrack_SurfacesMetadataAndStreamBlob() {
    var bik = BuildBik1(sampleRate: 11025, audioFlags: BinkAud16Bits, audioPacket: BuildZeroAudioPacket());
    using var ms = new MemoryStream(bik);
    var entries = new BikFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.bik" && e.Kind == "Container"), Is.True);

    var text = Encoding.UTF8.GetString(Extract(bik, "metadata.ini"));
    Assert.That(text, Does.Contain("signature = BIKi"));
    Assert.That(text, Does.Contain("audio_tracks = 1"));
    Assert.That(text, Does.Contain("sample_rate = 11025"));
    Assert.That(text, Does.Contain("codec = binkaudio_rdft"));
    Assert.That(text, Does.Contain("packets = 1"));
    Assert.That(text, Does.Contain("video_size = 2"));
    Assert.That(text, Does.Contain($"audio0_size = {BuildZeroAudioPacket().Length}"));

    Assert.That(entries.Any(e => e.Name == "TRACK0.bin" && e.Kind == "Stream"), Is.True);
    Assert.That(entries.Any(e => e.Name == "VIDEO.bin" && e.Kind == "Track"), Is.True);
    // The hand-built all-zero packet decodes to silence -> a per-channel WAV is surfaced.
    Assert.That(entries.Any(e => e.Name.StartsWith("TRACK0_") && e.Name.EndsWith(".wav") && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void ExtractedElementaryStreams_RemuxByteExactly() {
    var bik = BuildBik1(sampleRate: 11025, audioFlags: BinkAud16Bits, audioPacket: BuildZeroAudioPacket());
    var descriptor = new BikFormatDescriptor();

    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("metadata.ini", Extract(bik, "metadata.ini")),
      ArchiveInputInfo.InMemory("VIDEO.bin", Extract(bik, "VIDEO.bin")),
      ArchiveInputInfo.InMemory("TRACK0.bin", Extract(bik, "TRACK0.bin")),
    ];

    using var output = new MemoryStream();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(bik));
  }

  [Test]
  public void Mux_TwoVideoFrames_WritesOffsetsKeyframesAndLargestFrame() {
    const string metadata = """
      [Bink]
      signature = BIKi
      frames = 2
      reserved = 7
      width = 320
      height = 200
      fps_num = 25
      fps_den = 1
      video_flags = 3
      audio_tracks = 0
      [Frame0]
      keyframe = true
      video_size = 2
      [Frame1]
      keyframe = false
      video_size = 2
      """;
    var descriptor = new BikFormatDescriptor();
    using var output = new MemoryStream();

    descriptor.Create(output, [
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes(metadata)),
      ArchiveInputInfo.InMemory("VIDEO.bin", [0x10, 0x11, 0x20, 0x21]),
    ], new FormatCreateOptions());

    var muxed = output.ToArray();
    Assert.That(Encoding.ASCII.GetString(muxed, 0, 4), Is.EqualTo("BIKi"));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(muxed.AsSpan(8, 4)), Is.EqualTo(2));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(muxed.AsSpan(12, 4)), Is.EqualTo(2));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(muxed.AsSpan(16, 4)), Is.EqualTo(7));

    const int indexOffset = 44;
    var first = BinaryPrimitives.ReadUInt32LittleEndian(muxed.AsSpan(indexOffset, 4));
    var second = BinaryPrimitives.ReadUInt32LittleEndian(muxed.AsSpan(indexOffset + 4, 4));
    Assert.That((first & 1u) != 0, Is.True);
    Assert.That((second & 1u) != 0, Is.False);
    Assert.That(first & ~1u, Is.EqualTo(52u));
    Assert.That(second & ~1u, Is.EqualTo(54u));

    Assert.That(Extract(muxed, "VIDEO.bin"), Is.EqualTo(new byte[] { 0x10, 0x11, 0x20, 0x21 }));
  }

  [Test]
  public void Mux_LengthMismatch_IsRejected() {
    const string metadata = """
      [Bink]
      signature = BIKi
      frames = 1
      width = 320
      height = 200
      fps_num = 25
      fps_den = 1
      audio_tracks = 0
      [Frame0]
      video_size = 4
      """;
    var descriptor = new BikFormatDescriptor();
    using var output = new MemoryStream();

    Assert.That(() => descriptor.Create(output, [
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes(metadata)),
      ArchiveInputInfo.InMemory("VIDEO.bin", [0x10, 0x11]),
    ], new FormatCreateOptions()), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void FullContainer_CreatePath_IsByteExactPassthrough() {
    var bik = BuildBik1(11025, BinkAud16Bits, BuildZeroAudioPacket());
    using var output = new MemoryStream();

    new BikFormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("FULL.bik", bik)], new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(bik));
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
  public void Bink1K_ExtraHeader_RoundTripsThroughRemux() {
    var bik = BuildBik1(11025, BinkAud16Bits, BuildZeroAudioPacket(), revision: 'k', extraHeader: 0x12345678);
    var descriptor = new BikFormatDescriptor();
    using var output = new MemoryStream();

    descriptor.Create(output, [
      ArchiveInputInfo.InMemory("metadata.ini", Extract(bik, "metadata.ini")),
      ArchiveInputInfo.InMemory("VIDEO.bin", Extract(bik, "VIDEO.bin")),
      ArchiveInputInfo.InMemory("TRACK0.bin", Extract(bik, "TRACK0.bin")),
    ], new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(bik));
  }

  [Test]
  public void Truncated_DegradesGracefully() {
    var bik = BuildBik1(11025, BinkAud16Bits, BuildZeroAudioPacket());
    var truncated = bik[..30];
    using var ms = new MemoryStream(truncated);
    var entries = new BikFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.bik"), Is.True);
  }

  private static byte[] Extract(byte[] bik, string name) {
    using var output = new MemoryStream();
    new BikFormatDescriptor().ExtractEntry(new MemoryStream(bik), name, output, null);
    return output.ToArray();
  }

  /// <summary>Builds a minimal Bink 1/2 file: header + one audio track + a single frame.</summary>
  private static byte[] BuildBik1(int sampleRate, int audioFlags, byte[] audioPacket,
      string signature = "BIK", char revision = 'i', uint extraHeader = 0) {
    // Frame payload: audio_size(4) + packet, then two video bytes. The even frame size is
    // deliberate because bit zero of every frame offset belongs to the keyframe marker.
    var framePayload = new byte[4 + audioPacket.Length + 2];
    BinaryPrimitives.WriteUInt32LittleEndian(framePayload, (uint)audioPacket.Length);
    audioPacket.CopyTo(framePayload, 4);
    framePayload[^2] = 0xEE;
    framePayload[^1] = 0xEF;

    using var ms = new MemoryStream();
    void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
    void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }

    ms.WriteByte((byte)signature[0]);
    ms.WriteByte((byte)signature[1]);
    ms.WriteByte((byte)signature[2]);
    ms.WriteByte((byte)revision);

    U32(0);                            // file size (patched below to actual-8)
    U32(1);                            // num frames
    U32((uint)framePayload.Length);    // largest frame size
    U32(0);                            // reserved
    U32(320);                          // width
    U32(240);                          // height
    U32(15);                           // fps num
    U32(1);                            // fps den
    U32(0);                            // video flags (extradata)
    U32(1);                            // num audio tracks

    if ((signature == "BIK" && revision == 'k') ||
        (signature == "KB2" && revision is 'i' or 'j' or 'k'))
      U32(extraHeader);

    U32(0);                            // per-track max decoded packet bytes
    U16((ushort)sampleRate);
    U16((ushort)audioFlags);
    U32(0x100);                        // track id

    var indexPosOffset = (int)ms.Position;
    U32(0);                            // frame 0 start offset

    var frameDataStart = (int)ms.Position;
    ms.Write(framePayload);
    var fileEnd = (int)ms.Position;

    var buf = ms.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)(fileEnd - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(indexPosOffset), (uint)frameDataStart | 1u);
    return buf;
  }

  /// <summary>
  /// Builds an all-zero RDFT mono Bink Audio packet (frame_len 512 for 11025 Hz): a 4-byte
  /// reported-size prefix, two zero floats, band quantizers and width-0 runs.
  /// </summary>
  private static byte[] BuildZeroAudioPacket() {
    const int frameLen = 512;
    var sampleRateHalf = (11025 + 1) / 2;
    var bands = 1;
    int[] crit = [
      100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320,
      2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 24500,
    ];
    while (bands < 25 && sampleRateHalf > crit[bands - 1]) ++bands;

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
