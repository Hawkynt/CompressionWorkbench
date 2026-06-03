#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.OkiAdpcm;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Vox;

namespace Compression.Tests.Vox;

[TestFixture]
public class VoxTests {

  private static byte[] MakeVox(int byteCount) {
    var data = new byte[byteCount];
    // A gently varying nibble pattern decodes to a recognisable ramp/sine.
    for (var i = 0; i < byteCount; ++i)
      data[i] = (byte)(((i * 3) & 0x07) << 4 | ((i * 5) & 0x07));
    return data;
  }

  [Test]
  public void Descriptor_ListsFullMonoAndMetadata() {
    var blob = MakeVox(64);
    using var ms = new MemoryStream(blob);
    var entries = new VoxFormatDescriptor().List(ms, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.vox" && e.Kind == "Container"), Is.True);
      Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
      Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    });
  }

  [Test]
  public void Descriptor_ExtractedChannel_IsValidMonoRiffAt8000Hz() {
    var blob = MakeVox(64);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new VoxFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8), Is.True);
      Assert.That(wav.AsSpan(8, 4).SequenceEqual("WAVE"u8), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)VoxFormatDescriptor.AssumedSampleRate));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16), "16-bit decoded");
    });

    // The decoded sample count is two per VOX byte.
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(blob.Length * 2 * 2))); // 2 samples/byte * 2 bytes/sample
  }

  [Test]
  public void Create_FromMonoWav_RoundTripsWithinTolerance() {
    // Build a mono 16-bit WAV of a sine ramp.
    const int n = 2000;
    var pcm = new short[n];
    for (var i = 0; i < n; ++i)
      pcm[i] = (short)(5000.0 * i / n * Math.Sin(2 * Math.PI * i / 48.0));
    var le = new byte[n * 2];
    for (var i = 0; i < n; ++i) BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), pcm[i]);
    var wavBlob = PcmCodec.ToWavBlob(le, channels: 1, VoxFormatDescriptor.AssumedSampleRate, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wavBlob) };
    using var voxOut = new MemoryStream();
    new VoxFormatDescriptor().Create(voxOut, inputs, new FormatCreateOptions());
    var vox = voxOut.ToArray();

    Assert.That(vox.Length, Is.EqualTo((n + 1) / 2), "two samples per VOX byte");

    var decoded = OkiAdpcmCodec.Decode(vox);
    var maxErr = 0;
    for (var i = 0; i < n; ++i) maxErr = Math.Max(maxErr, Math.Abs(pcm[i] - decoded[i]));
    Assert.That(maxErr, Is.LessThan(1500), $"VOX round-trip max error {maxErr}");
  }

  [Test]
  public void Create_PassesThroughFullVox() {
    var blob = MakeVox(40);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.vox", blob) };
    using var voxOut = new MemoryStream();
    new VoxFormatDescriptor().Create(voxOut, inputs, new FormatCreateOptions());
    Assert.That(voxOut.ToArray(), Is.EqualTo(blob));
  }
}
