#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Nelly;

namespace Compression.Tests.Nelly;

/// <summary>
/// Pseudo-archive tests for the Nellymoser descriptor: a whole number of 64-byte
/// blocks surfaces <c>FULL.nelly</c> + <c>MONO.wav</c> + <c>metadata.ini</c> (at the
/// assumed 22050 Hz); a ragged stream falls back to <c>FULL.nelly</c> + metadata.
/// </summary>
[TestFixture]
public class NellyFormatTests {

  private const int BlockLen = 64;

  [Test]
  public void List_WholeBlocks_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(new byte[BlockLen * 2]);
    var entries = new NellyFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.nelly" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
  }

  [Test]
  public void Extract_FullNelly_RoundTripsBytes() {
    var blob = Enumerable.Range(0, BlockLen).Select(i => (byte)i).ToArray();
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new NellyFormatDescriptor().ExtractEntry(input, "FULL.nelly", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(blob));
  }

  [Test]
  public void Extract_MonoWav_IsValidRiffAt22050Hz() {
    using var input = new MemoryStream(new byte[BlockLen]);
    using var output = new MemoryStream();
    new NellyFormatDescriptor().ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(22050u));
    // 256 samples/block * 2 bytes.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)), Is.EqualTo(256u * 2));
  }

  [Test]
  public void List_RaggedStream_FallsBackToFullPlusMetadata() {
    // 64 + 5 bytes is not a whole number of blocks → no decoded channel.
    using var ms = new MemoryStream(new byte[BlockLen + 5]);
    var entries = new NellyFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.nelly"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
  }

  [Test]
  public void Metadata_RecordsBlockCountAndAssumedRate() {
    using var input = new MemoryStream(new byte[BlockLen * 3]);
    using var output = new MemoryStream();
    new NellyFormatDescriptor().ExtractEntry(input, "metadata.ini", output, null);
    var meta = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(meta, Does.Contain("blocks=3"));
    Assert.That(meta, Does.Contain("assumed_sample_rate=22050"));
    Assert.That(meta, Does.Contain("decoded=true"));
  }
}
