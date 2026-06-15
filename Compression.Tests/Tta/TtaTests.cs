#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Tta;
using Compression.Registry;
using FileFormat.Tta;

namespace Compression.Tests.Tta;

[TestFixture]
public class TtaTests {

  private static byte[] MakeStereoTta(int frames = 4096) {
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 100 % 30000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(-(i * 100 % 30000)));
    }

    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    TtaCodec.Compress(input, output, 2, 44100, 16);
    return output.ToArray();
  }

  private static byte[] MakeSixChannelTta(int frames = 2048) {
    const int ch = 6;
    var pcm = new byte[frames * ch * 2];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < ch; ++c)
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * ch + c) * 2), (short)((i * (c + 1)) % 20000 - 10000));

    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    TtaCodec.Compress(input, output, ch, 48000, 16);
    return output.ToArray();
  }

  [Test]
  public void Descriptor_ListsFullAndChannels_Stereo() {
    var tta = MakeStereoTta();
    using var ms = new MemoryStream(tta);
    var entries = new TtaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.tta"), Is.True, "Should contain FULL.tta");
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True, "Should contain LEFT.wav");
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True, "Should contain RIGHT.wav");
    Assert.That(entries.First(e => e.Name == "FULL.tta").Method, Is.EqualTo("tta"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Method, Is.EqualTo("pcm"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Descriptor_SurfacesMetadataIni() {
    var tta = MakeStereoTta();
    using var ms = new MemoryStream(tta);
    var entries = new TtaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Descriptor_SixChannel_SurfacesPer5Point1Names() {
    var tta = MakeSixChannelTta();
    using var ms = new MemoryStream(tta);
    var entries = new TtaFormatDescriptor().List(ms, null);

    // FFmpeg 5.1 default layout: FL FR FC LFE BL BR.
    foreach (var name in new[] { "FRONT_LEFT.wav", "FRONT_RIGHT.wav", "CENTER.wav", "LFE.wav", "BACK_LEFT.wav", "BACK_RIGHT.wav" })
      Assert.That(entries.Any(e => e.Name == name), Is.True, $"missing {name}");
  }

  [Test]
  public void Descriptor_ExtractedChannelIsValidMonoWav() {
    var tta = MakeStereoTta();
    using var ms = new MemoryStream(tta);
    using var output = new MemoryStream();
    new TtaFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
  }

  [Test]
  public void Descriptor_GracefulFallback_OnNonDecodableInput() {
    // Valid-looking TTA1 header guarded by a correct CRC, but no real frames.
    var blob = new byte[22];
    blob[0] = (byte)'T'; blob[1] = (byte)'T'; blob[2] = (byte)'A'; blob[3] = (byte)'1';
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(10), 44100);
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(14), 1000);
    // Header CRC left zero on purpose → decode throws → FULL-only listing.

    using var ms = new MemoryStream(blob);
    var entries = new TtaFormatDescriptor().List(ms, null);

    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.tta"));
  }

  [Test]
  public void Create_RoundTrip_SplitAssembleSplit_IsByteExact() {
    var tta = MakeStereoTta();

    // Split into per-channel WAVs.
    using var ms = new MemoryStream(tta);
    var descriptor = new TtaFormatDescriptor();
    var entries = descriptor.List(ms, null);
    var channelNames = entries.Where(e => e.Kind == "Channel").Select(e => e.Name).ToList();
    Assert.That(channelNames, Is.EquivalentTo(new[] { "LEFT.wav", "RIGHT.wav" }));

    var inputs = new List<ArchiveInputInfo>();
    foreach (var name in channelNames) {
      using var src = new MemoryStream(tta);
      using var chOut = new MemoryStream();
      descriptor.ExtractEntry(src, name, chOut, null);
      inputs.Add(ArchiveInputInfo.InMemory(name, chOut.ToArray()));
    }

    // Assemble a fresh .tta from the channel WAVs.
    using var assembled = new MemoryStream();
    descriptor.Create(assembled, inputs, new FormatCreateOptions());
    var rebuilt = assembled.ToArray();

    // Decode both the original and rebuilt .tta and compare the PCM byte-exactly.
    using var origIn = new MemoryStream(tta);
    using var origPcm = new MemoryStream();
    TtaCodec.Decompress(origIn, origPcm);

    using var newIn = new MemoryStream(rebuilt);
    using var newPcm = new MemoryStream();
    TtaCodec.Decompress(newIn, newPcm);

    Assert.That(newPcm.ToArray(), Is.EqualTo(origPcm.ToArray()));
  }

  [Test]
  public void Create_PassesThroughFullTta() {
    var tta = MakeStereoTta();
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.tta", tta) };

    using var output = new MemoryStream();
    new TtaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(tta));
  }
}
