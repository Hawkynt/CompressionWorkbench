#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Constraints;

[TestFixture]
public class WriteConstraintsTests {

  [Test]
  public void Mp3_AcceptsCoverJpg() {
    var desc = new FileFormat.Mp3.Mp3FormatDescriptor();
    var input = new ArchiveInputInfo(@"C:\tmp\cover.jpg", "cover.jpg", false);
    Assert.That(desc.CanAccept(input, out var why), Is.True, why);
  }

  [Test]
  public void Mp3_RejectsDocx() {
    var desc = new FileFormat.Mp3.Mp3FormatDescriptor();
    var input = new ArchiveInputInfo(@"C:\tmp\resume.docx", "resume.docx", false);
    Assert.That(desc.CanAccept(input, out var why), Is.False);
    Assert.That(why, Is.Not.Null);
  }

  [Test]
  public void Mp3_AcceptsId3v1SubfolderMetadata() {
    var desc = new FileFormat.Mp3.Mp3FormatDescriptor();
    var input = new ArchiveInputInfo(@"C:\tmp\metadata.ini", "id3v1/metadata.ini", false);
    Assert.That(desc.CanAccept(input, out _), Is.True);
  }

  [Test]
  public void Wav_AcceptsChannelFile() {
    var desc = new FileFormat.Wav.WavFormatDescriptor();
    Assert.That(desc.CanAccept(new ArchiveInputInfo(@"C:\tmp\LEFT.wav", "LEFT.wav", false), out _), Is.True);
    Assert.That(desc.CanAccept(new ArchiveInputInfo(@"C:\tmp\cover.jpg", "cover.jpg", false), out _), Is.False);
  }

  [Test]
  public void D64_HasMaxSizeLimit() {
    var desc = new FileSystem.D64.D64FormatDescriptor();
    Assert.That(desc.MaxTotalArchiveSize, Is.EqualTo(174848));
    Assert.That(desc.AcceptedInputsDescription, Does.Contain("1541"));
  }

  [Test]
  public void D71_HasDoubleD64Size() {
    var desc = new FileSystem.D71.D71FormatDescriptor();
    Assert.That(desc.MaxTotalArchiveSize, Is.EqualTo(349696));
  }

  [Test]
  public void D81_Has800KSize() {
    var desc = new FileSystem.D81.D81FormatDescriptor();
    Assert.That(desc.MaxTotalArchiveSize, Is.EqualTo(819200));
  }

  [Test]
  public void Adf_HasAmigaDdSize() {
    var desc = new FileSystem.Adf.AdfFormatDescriptor();
    Assert.That(desc.MaxTotalArchiveSize, Is.EqualTo(901120));
  }

  [Test]
  public void Midi_AcceptsOnlyMidiFiles() {
    var desc = new FileFormat.Midi.MidiFormatDescriptor();
    Assert.That(desc.CanAccept(new ArchiveInputInfo(@"C:\tmp\track_00_Bass.mid", "track_00_Bass.mid", false), out _), Is.True);
    Assert.That(desc.CanAccept(new ArchiveInputInfo(@"C:\tmp\metadata.ini", "metadata.ini", false), out _), Is.True);
    Assert.That(desc.CanAccept(new ArchiveInputInfo(@"C:\tmp\hello.wav", "hello.wav", false), out var why), Is.False);
    Assert.That(why, Is.Not.Null);
  }
}
