#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Gym;

namespace Compression.Tests.Gym;

[TestFixture]
public class GymTests {

  // Builds a 428-byte GYMX header + a short command stream.
  private static byte[] MakeHeaderGym(out byte[] commandStream) {
    commandStream = [0x00, 0x52, 0x22, 0x00, 0x01];
    var buf = new byte[428 + commandStream.Length];
    "GYMX"u8.ToArray().CopyTo(buf.AsSpan(0, 4));
    void Ascii(int off, string s) {
      var a = Encoding.ASCII.GetBytes(s);
      Buffer.BlockCopy(a, 0, buf, off, a.Length);
    }
    Ascii(0x04, "GymSong");
    Ascii(0x24, "GymGame");
    Ascii(0x44, "Copyright 2026");
    Ascii(0x64, "Emu");
    Ascii(0x84, "GymDumper");
    Ascii(0xA4, "Some comment");
    Buffer.BlockCopy(commandStream, 0, buf, 428, commandStream.Length);
    return buf;
  }

  // Headerless raw command stream.
  private static byte[] MakeHeaderlessGym() => [0x00, 0x52, 0x22, 0x00, 0x52, 0x28, 0x80, 0x01];

  [Test]
  public void List_Header_ExposesFullMetadataStream() {
    using var ms = new MemoryStream(MakeHeaderGym(out _));
    var entries = new GymFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.gym"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "command_stream.bin"), Is.True);
  }

  [Test]
  public void Extract_Header_FullByteIdentical_TagsParsed() {
    var blob = MakeHeaderGym(out var cs);
    var tmp = Path.Combine(Path.GetTempPath(), "gym_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new GymFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.gym")), Is.EqualTo(blob));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "command_stream.bin")), Is.EqualTo(cs));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("has_header = true"));
      Assert.That(meta, Does.Contain("song = GymSong"));
      Assert.That(meta, Does.Contain("game = GymGame"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Headerless_TreatedAsRawStream() {
    var blob = MakeHeaderlessGym();
    var tmp = Path.Combine(Path.GetTempPath(), "gymh_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new GymFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "command_stream.bin")), Is.EqualTo(blob));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("has_header = false"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Empty_DoesNotThrow() {
    using var ms = new MemoryStream([]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new GymFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.gym"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_Magic() {
    var d = new GymFormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("GYMX"u8.ToArray()));
    Assert.That(d.Extensions, Does.Contain(".gym"));
  }
}
