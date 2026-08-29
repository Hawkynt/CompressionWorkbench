#pragma warning disable CS1591
using Codec.Midi;
using Compression.Registry;
using FileFormat.Midi;

namespace Compression.Tests.Midi;

[TestFixture]
public class MidiWriteTests {

  [Test]
  public void MidiWriter_MultiTrack_PreservesBodiesAndHeader() {
    byte[] conductor = [0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, 0x00, 0xFF, 0x2F, 0x00];
    byte[] notes = [0x00, 0x90, 0x3C, 0x64, 0x60, 0x80, 0x3C, 0x00, 0x00, 0xFF, 0x2F, 0x00];

    var blob = MidiWriter.BuildFile([conductor, notes], division: 96, format: 1);
    var codec = new MidiCodec();
    var header = codec.ReadHeader(blob);
    var tracks = codec.FindTracks(blob);

    Assert.That(header.Format, Is.EqualTo(1));
    Assert.That(header.NumTracks, Is.EqualTo(2));
    Assert.That(header.Division, Is.EqualTo(96));
    Assert.That(codec.ExtractTrackBytes(blob, tracks[0]), Is.EqualTo(conductor));
    Assert.That(codec.ExtractTrackBytes(blob, tracks[1]), Is.EqualTo(notes));
  }

  [Test]
  public void Descriptor_Create_ReassemblesExtractedTracksByteExactly() {
    byte[] first = [0x00, 0xFF, 0x03, 0x01, (byte)'A', 0x00, 0xFF, 0x2F, 0x00];
    byte[] second = [0x00, 0xFF, 0x03, 0x01, (byte)'B', 0x00, 0xFF, 0x2F, 0x00];
    var source = MidiWriter.BuildFile([first, second], division: 480, format: 1);
    var descriptor = new MidiFormatDescriptor();

    var inputs = new List<ArchiveInputInfo>();
    foreach (var name in new[] { "track_00_A.mid", "track_01_B.mid" }) {
      using var input = new MemoryStream(source);
      using var extracted = new MemoryStream();
      descriptor.ExtractEntry(input, name, extracted, null);
      inputs.Add(ArchiveInputInfo.InMemory(name, extracted.ToArray()));
    }

    using var output = new MemoryStream();
    descriptor.Create(output, inputs, new FormatCreateOptions());
    var rebuilt = output.ToArray();

    var codec = new MidiCodec();
    var header = codec.ReadHeader(rebuilt);
    var tracks = codec.FindTracks(rebuilt);
    Assert.That(header.Format, Is.EqualTo(1));
    Assert.That(header.NumTracks, Is.EqualTo(2));
    Assert.That(header.Division, Is.EqualTo(480));
    Assert.That(codec.ExtractTrackBytes(rebuilt, tracks[0]), Is.EqualTo(first));
    Assert.That(codec.ExtractTrackBytes(rebuilt, tracks[1]), Is.EqualTo(second));
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test]
  public void Descriptor_Create_RejectsMismatchedTrackDivisions() {
    byte[] eot = [0x00, 0xFF, 0x2F, 0x00];
    var inputs = new[] {
      ArchiveInputInfo.InMemory("track_00_a.mid", MidiWriter.BuildFile([eot], division: 96, format: 0)),
      ArchiveInputInfo.InMemory("track_01_b.mid", MidiWriter.BuildFile([eot], division: 120, format: 0)),
    };

    using var output = new MemoryStream();
    Assert.Throws<InvalidOperationException>(() =>
      new MidiFormatDescriptor().Create(output, inputs, new FormatCreateOptions()));
  }

  [Test]
  public void Descriptor_Create_FullMidi_PassesThroughAfterValidation() {
    byte[] eot = [0x00, 0xFF, 0x2F, 0x00];
    var source = MidiWriter.BuildFile([eot], division: -6360, format: 0); // SMPTE-form division
    using var output = new MemoryStream();

    new MidiFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("FULL.mid", source)], new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(source));
  }
}
