using System.Text;
using Compression.Registry;
using FileSystem.BeeGfs;

namespace Compression.Tests.BeeGfs;

[TestFixture]
public class BeeGfsDetectionTests {

  private static byte[] BuildMinimal(int payloadLen = 128) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("BeeGFS").CopyTo(image.AsSpan(0, 6));
    for (var i = 0; i < payloadLen; i++) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new BeeGfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("BeeGfs"));
    Assert.That(d.Extensions, Does.Contain(".beegfs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("BeeGFS"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo("BeeG"u8.ToArray()));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new BeeGfsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "beegfs-chunk.bin" }));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new BeeGfsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("stage 0"));
    Assert.That(desc, Does.Contain("detection"));
    // Per CONTRIBUTING.md staging strategy: Stage-0 must NOT advertise create/modify.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Stub")]
  public void Description_ExplainsWhyStageZero() {
    var d = new BeeGfsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    // Honest Stage-0 acceptance gate: the descriptor must surface the
    // architectural reason promotion is not possible (no single-image surface).
    Assert.That(desc, Does.Contain("no standalone on-disk image"));
    Assert.That(desc, Does.Contain("distributed").Or.Contain("cluster"));
  }

  [Test, Category("Exception")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[4]);
    Assert.Throws<InvalidDataException>(() => _ = new BeeGfsReader(ms));
  }

  [Test, Category("Exception")]
  public void Reader_BadMagic_Throws() {
    var bad = new byte[32];
    Encoding.ASCII.GetBytes("NOTBEEGFS").CopyTo(bad.AsSpan(0));
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new BeeGfsReader(ms));
  }
}
