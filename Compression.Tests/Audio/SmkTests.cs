#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Smk;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the Smacker container descriptor and its packet-preserving mux/remux path. The tests use
/// a hand-built minimal SMK4 so header, frame-table, packet-boundary and replacement behavior are
/// deterministic without depending on a Smacker video encoder.
/// </summary>
[TestFixture]
public class SmkTests {

  private const int SmkAudPacked = 0x80;

  [Test]
  public void Smk_SmkaMonoTrack_SurfacesMetadataStreamPacketBundleAndChannel() {
    var smk = BuildSmk(sampleRate: 22050, aflag: SmkAudPacked, audioChunkPayload: BuildSmka8BitMono());
    using var ms = new MemoryStream(smk);
    var entries = new SmkFormatDescriptor().List(ms, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.smk" && e.Kind == "Container"), Is.True);
      Assert.That(entries.Any(e => e.Name == "HEADER.bin" && e.Kind == "Structure"), Is.True);
      Assert.That(entries.Any(e => e.Name == "FRAME_SIZES.bin" && e.Kind == "Structure"), Is.True);
      Assert.That(entries.Any(e => e.Name == "FRAME_TYPES.bin" && e.Kind == "Structure"), Is.True);
      Assert.That(entries.Any(e => e.Name == "HUFFMAN.bin" && e.Kind == "Structure"), Is.True);
      Assert.That(entries.Any(e => e.Name == "TRACK0.packets" && e.Kind == "PacketStream"), Is.True);
      Assert.That(entries.Any(e => e.Name == "TRACK0.bin" && e.Kind == "Stream"), Is.True);
      Assert.That(entries.Any(e => e.Name == "TRACK0_MONO.wav" && e.Kind == "Channel"), Is.True);
    });

    var text = Encoding.UTF8.GetString(Extract(smk, "metadata.ini"));
    Assert.That(text, Does.Contain("magic = SMK4"));
    Assert.That(text, Does.Contain("sample_rate = 22050"));
    Assert.That(text, Does.Contain("codec = smackaud"));
    Assert.That(text, Does.Contain("chunks = 1"));
  }

  [Test]
  public void Descriptor_AdvertisesCreationThroughTheCapabilityInterface() {
    var descriptor = new SmkFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    });
  }

  [Test]
  public void StructuralEntries_Create_RoundTripByteExact() {
    var smk = BuildSmk(22050, SmkAudPacked, BuildSmka8BitMono());
    var names = new[] { "HEADER.bin", "FRAME_SIZES.bin", "FRAME_TYPES.bin", "HUFFMAN.bin", "VIDEO.bin" };
    var inputs = names.Select(name => ArchiveInputInfo.InMemory(name, Extract(smk, name))).ToArray();

    var rebuilt = Create(inputs);

    Assert.That(rebuilt, Is.EqualTo(smk));
  }

  [Test]
  public void FullContainer_WithUnchangedPacketBundle_RemainsByteExact() {
    var smk = BuildSmk(22050, SmkAudPacked, BuildSmka8BitMono());
    var packets = Extract(smk, "TRACK0.packets");

    var rebuilt = Create([
      ArchiveInputInfo.InMemory("FULL.smk", smk),
      ArchiveInputInfo.InMemory("TRACK0.packets", packets),
    ]);

    Assert.That(rebuilt, Is.EqualTo(smk));
  }

  [Test]
  public void FullContainer_WithReplacementPacket_RebuildsFrameAndAudioSize() {
    var smk = BuildSmk(22050, SmkAudPacked, BuildSmka8BitMono());
    var replacement = BuildSmka8BitMono().Concat(new byte[] { 0xAA, 0x55, 0x11 }).ToArray();
    var packets = BuildPacketBundle((0, replacement));

    var rebuilt = Create([
      ArchiveInputInfo.InMemory("FULL.smk", smk),
      ArchiveInputInfo.InMemory("TRACK0.packets", packets),
    ]);

    Assert.Multiple(() => {
      Assert.That(Extract(rebuilt, "TRACK0.bin"), Is.EqualTo(replacement));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(24)), Is.EqualTo(4u),
        "AudioSize[0] is the maximum unpacked packet size for compressed Smacker audio");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(104)) & 3u, Is.Zero,
        "the rebuilt physical frame remains dword-aligned while preserving the low flag bits");
      Assert.That(rebuilt[108] & 0x02, Is.EqualTo(0x02), "frame type bit 1 declares track 0");
    });
  }

  [Test]
  public void EmptyPacketBundle_RemovesTrackPacketsWithoutInventingAHeaderTrack() {
    var smk = BuildSmk(22050, SmkAudPacked, BuildSmka8BitMono());
    var rebuilt = Create([
      ArchiveInputInfo.InMemory("FULL.smk", smk),
      ArchiveInputInfo.InMemory("TRACK0.packets", BuildPacketBundle()),
    ]);

    Assert.Multiple(() => {
      Assert.That(Extract(rebuilt, "TRACK0.bin"), Is.Empty);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(24)), Is.Zero);
      Assert.That(rebuilt[108] & 0x02, Is.Zero, "track 0 frame-presence bit must be cleared");
    });
  }

  [Test]
  public void PacketOverride_ForUndeclaredTrack_IsRejected() {
    var payload = BuildSmka8BitMono();
    var smk = BuildSmk(22050, SmkAudPacked, payload);
    var inputs = new ArchiveInputInfo[] {
      ArchiveInputInfo.InMemory("FULL.smk", smk),
      ArchiveInputInfo.InMemory("TRACK1.packets", BuildPacketBundle((0, payload))),
    };

    using var output = new MemoryStream();
    var creator = (IArchiveCreatable)new SmkFormatDescriptor();
    var exception = Assert.Throws<InvalidDataException>(() => creator.Create(output, inputs, new FormatCreateOptions()));
    Assert.That(exception!.Message, Does.Contain("track 1").IgnoreCase);
  }

  [Test]
  public void PacketBundle_WithDuplicateFrame_IsRejected() {
    var payload = BuildSmka8BitMono();
    var smk = BuildSmk(22050, SmkAudPacked, payload);
    var inputs = new ArchiveInputInfo[] {
      ArchiveInputInfo.InMemory("FULL.smk", smk),
      ArchiveInputInfo.InMemory("TRACK0.packets", BuildPacketBundle((0, payload), (0, payload))),
    };

    using var output = new MemoryStream();
    var creator = (IArchiveCreatable)new SmkFormatDescriptor();
    var exception = Assert.Throws<InvalidDataException>(() => creator.Create(output, inputs, new FormatCreateOptions()));
    Assert.That(exception!.Message, Does.Contain("more packets than the container has frames").Or.Contain("more than once"));
  }

  [Test]
  public void Truncated_DegradesGracefully() {
    var smk = BuildSmk(22050, SmkAudPacked, BuildSmka8BitMono());
    using var ms = new MemoryStream(smk[..40]);
    var entries = new SmkFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.smk"), Is.True);
  }

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs) {
    using var output = new MemoryStream();
    ((IArchiveCreatable)new SmkFormatDescriptor()).Create(output, inputs, new FormatCreateOptions());
    return output.ToArray();
  }

  private static byte[] Extract(byte[] smk, string name) {
    using var output = new MemoryStream();
    new SmkFormatDescriptor().ExtractEntry(new MemoryStream(smk, writable: false), name, output, null);
    return output.ToArray();
  }

  private static byte[] BuildPacketBundle(params (int FrameIndex, byte[] Payload)[] packets) {
    using var output = new MemoryStream();
    output.Write("SMKAPKT1"u8);
    WriteUInt32(output, checked((uint)packets.Length));
    foreach (var (frameIndex, payload) in packets) {
      WriteUInt32(output, checked((uint)frameIndex));
      WriteUInt32(output, checked((uint)payload.Length));
      output.Write(payload);
    }
    return output.ToArray();
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  /// <summary>Builds a minimal SMK4 with one audio track and a single frame.</summary>
  private static byte[] BuildSmk(int sampleRate, int aflag, byte[] audioChunkPayload) {
    var chunkSize = 4 + audioChunkPayload.Length;
    var paddedLen = (chunkSize + 3) & ~3;
    var frameData = new byte[paddedLen];
    BinaryPrimitives.WriteUInt32LittleEndian(frameData, (uint)chunkSize);
    audioChunkPayload.CopyTo(frameData, 4);

    using var ms = new MemoryStream();
    void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }

    ms.Write("SMK4"u8);
    U32(8);
    U32(8);
    U32(1);
    U32(100);
    U32(0);

    for (var i = 0; i < 28; ++i) ms.WriteByte(0);
    U32(0);
    for (var i = 0; i < 16; ++i) ms.WriteByte(0);

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

    U32(0);
    U32((uint)frameData.Length);
    ms.WriteByte(0x02);
    ms.Write(frameData);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds an 8-bit mono SMKA packet payload: its 4-byte unpacked-size prefix plus the
  /// LSB-first bitstream for samples 10, 12, 212, 214.
  /// </summary>
  private static byte[] BuildSmka8BitMono() {
    var body = new LeBitWriter();
    body.Put(1, 1);
    body.Put(1, 0);
    body.Put(1, 0);
    body.Put(1, 0);
    body.Put(1, 1);
    body.Put(1, 0);
    body.Put(8, 2);
    body.Put(1, 0);
    body.Put(8, 200);
    body.Put(1, 0);
    body.Put(8, 10);
    body.Put(1, 0);
    body.Put(1, 1);
    body.Put(1, 0);
    var bits = body.ToArray();

    var payload = new byte[4 + bits.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(payload, 4);
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
