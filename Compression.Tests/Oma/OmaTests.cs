#pragma warning disable CS1591
using System.Text;
using FileFormat.Oma;

namespace Compression.Tests.Oma;

/// <summary>
/// Pins the read-only Sony OpenMG (OMA/AA3) stream-info descriptor. Hand-crafted files (leading
/// "ea3" ID3v2-style tag with text frames + the 96-byte "EA3" binary header + a coded payload)
/// exercise codec-id naming, ATRAC3 sample-rate decoding from the coding params, tag text
/// extraction, payload slicing, and graceful handling of garbage.
/// </summary>
[TestFixture]
public class OmaTests {

  /// <summary>
  /// Builds an OpenMG file: an "ea3" ID3v2.4 tag (with optional TIT2/TPE1 text frames) followed
  /// by the 96-byte "EA3" header (codec id at byte 32, 24-bit coding params at 33..35) and a
  /// coded payload of <paramref name="payloadLength"/> filler bytes.
  /// </summary>
  private static byte[] BuildOma(int codecId, int codingParams, int payloadLength,
      (string Id, string Text)[]? frames = null) {
    var tagBody = new List<byte>();
    foreach (var (id, text) in frames ?? []) {
      var payload = new List<byte> { 0x03 }; // UTF-8 encoding marker
      payload.AddRange(Encoding.UTF8.GetBytes(text));
      tagBody.AddRange(Encoding.ASCII.GetBytes(id));
      var size = payload.Count;
      tagBody.AddRange([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size]);
      tagBody.AddRange([0, 0]); // frame flags
      tagBody.AddRange(payload);
    }

    var tagSize = tagBody.Count;
    var ssize = new byte[] {
      (byte)((tagSize >> 21) & 0x7F), (byte)((tagSize >> 14) & 0x7F),
      (byte)((tagSize >> 7) & 0x7F), (byte)(tagSize & 0x7F),
    };

    var file = new List<byte>();
    file.AddRange("ea3"u8.ToArray());
    file.AddRange([0x04, 0x00, 0x00]); // version + flags
    file.AddRange(ssize);
    file.AddRange(tagBody);

    var ea3 = new byte[96];
    ea3[0] = (byte)'E'; ea3[1] = (byte)'A'; ea3[2] = (byte)'3';
    ea3[32] = (byte)codecId;
    ea3[33] = (byte)(codingParams >> 16);
    ea3[34] = (byte)(codingParams >> 8);
    ea3[35] = (byte)codingParams;
    file.AddRange(ea3);

    file.AddRange(Enumerable.Range(0, payloadLength).Select(i => (byte)(i & 0xFF)));
    return file.ToArray();
  }

  private static string MetadataOf(byte[] blob) {
    using var input = new MemoryStream(blob);
    using var meta = new MemoryStream();
    new OmaFormatDescriptor().ExtractEntry(input, "metadata.ini", meta, null);
    return Encoding.UTF8.GetString(meta.ToArray());
  }

  [Test]
  public void List_SurfacesFullStreamAndMetadata() {
    var blob = BuildOma(codecId: 0, codingParams: 0x002000, payloadLength: 64);
    using var ms = new MemoryStream(blob);
    var entries = new OmaFormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.oma");
    Assert.That(full.Kind, Is.EqualTo("Container"));
    var stream = entries.Single(e => e.Name == "stream.bin");
    Assert.That(stream.Kind, Is.EqualTo("Stream"));
    Assert.That(stream.Method, Is.EqualTo("ATRAC3"), "stream method = carried codec name");
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Metadata_NamesCodecById() {
    Assert.That(MetadataOf(BuildOma(0, 0, 16)), Does.Contain("codec=ATRAC3"));
    Assert.That(MetadataOf(BuildOma(1, 0, 16)), Does.Contain("codec=ATRAC3plus"));
    Assert.That(MetadataOf(BuildOma(3, 0, 16)), Does.Contain("codec=MP3"));
    Assert.That(MetadataOf(BuildOma(4, 0, 16)), Does.Contain("codec=LPCM"));
  }

  [Test]
  public void Metadata_DecodesAtrac3SampleRateFromCodingParams() {
    // Rate index is bits 13..15 of the 24-bit coding params. Index 1 → 44100 Hz.
    var codingParams = 1 << 13;
    var text = MetadataOf(BuildOma(codecId: 0, codingParams: codingParams, payloadLength: 32));
    Assert.That(text, Does.Contain("sample_rate=44100"));
  }

  [Test]
  public void Metadata_ExtractsTagTextFrames() {
    var blob = BuildOma(codecId: 0, codingParams: 0, payloadLength: 16,
      frames: [("TIT2", "Track Title"), ("TPE1", "The Artist")]);
    var text = MetadataOf(blob);
    Assert.That(text, Does.Contain("TIT2=Track Title"));
    Assert.That(text, Does.Contain("TPE1=The Artist"));
  }

  [Test]
  public void Stream_PayloadIsSlicedAfterEa3Header() {
    var blob = BuildOma(codecId: 1, codingParams: 0, payloadLength: 100);
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new OmaFormatDescriptor().ExtractEntry(input, "stream.bin", output, null);
    var payload = output.ToArray();
    Assert.That(payload.Length, Is.EqualTo(100), "payload = everything after the 96-byte EA3 header");
    Assert.That(payload[0], Is.EqualTo(0));
    Assert.That(payload[1], Is.EqualTo(1));
  }

  [Test]
  public void Atrac3_StereoPayload_SurfacesDecodedChannelWavs() {
    // ATRAC3 (codec id 0), 44100 Hz (rate idx 1), low-10 bits = 24 words → 192-byte stereo
    // frame (96 bytes/channel, a valid LP2 size). Two frames of filler bytes decode to two
    // bounded channel WAVs.
    var codingParams = (1 << 13) | 24;
    var blob = BuildOma(codecId: 0, codingParams: codingParams, payloadLength: 192 * 2);

    var entries = new OmaFormatDescriptor().List(new MemoryStream(blob), null);
    var channelEntries = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channelEntries.Count, Is.EqualTo(2), "stereo ATRAC3 surfaces two channel WAVs");

    var first = channelEntries[0];
    using var wavStream = new MemoryStream();
    new OmaFormatDescriptor().ExtractEntry(new MemoryStream(blob), first.Name, wavStream, null);
    var wav = wavStream.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    // 2 frames × 1024 samples × 2 bytes + 44-byte header.
    Assert.That(wav.Length, Is.EqualTo(44 + 2 * 1024 * 2));
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)),
      Is.EqualTo(44100u));
  }

  [Test]
  public void Atrac3_SilentPayload_DecodesToSilentChannels() {
    // An all-zero coded payload selects the unencoded / no-gain / no-tonal path → silence.
    var blob = BuildOma(codecId: 0, codingParams: (1 << 13) | 24, payloadLength: 192 * 2);
    // Overwrite the filler payload (after the 96-byte EA3 header that follows the empty tag)
    // with zeros.
    var payloadOffset = 10 + 0 + 96; // ea3 tag header (10) + empty tag (0) + EA3 header (96)
    for (var i = payloadOffset; i < blob.Length; ++i)
      blob[i] = 0;

    var entries = new OmaFormatDescriptor().List(new MemoryStream(blob), null);
    var channel = entries.First(e => e.Kind == "Channel");
    using var wavStream = new MemoryStream();
    new OmaFormatDescriptor().ExtractEntry(new MemoryStream(blob), channel.Name, wavStream, null);
    var wav = wavStream.ToArray();
    Assert.That(wav.Skip(44).All(b => b == 0), Is.True);
  }

  [Test]
  public void Atrac3Plus_PayloadStaysUndecoded() {
    // Codec id 1 (ATRAC3plus) is not decoded — only the stream blob is surfaced.
    var blob = BuildOma(codecId: 1, codingParams: (1 << 13) | 24, payloadLength: 192 * 2);
    var entries = new OmaFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
    Assert.That(entries.Any(e => e.Name == "stream.bin"), Is.True);
  }

  [Test]
  public void Garbage_IsHandledGracefully() {
    var junk = Encoding.ASCII.GetBytes("not an OpenMG file");
    using var ms = new MemoryStream(junk);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new OmaFormatDescriptor().List(ms, null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.oma"), Is.True);
    Assert.That(MetadataOf(junk), Does.Contain("codec=unknown"));
  }
}
