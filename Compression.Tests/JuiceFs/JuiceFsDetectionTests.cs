using System.Text;
using Compression.Registry;
using FileSystem.JuiceFs;

namespace Compression.Tests.JuiceFs;

[TestFixture]
public class JuiceFsDetectionTests {

  private static byte[] BuildMinimal(int payloadLen = 128) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("JuiceFS").CopyTo(image.AsSpan(0, 7));
    for (var i = 0; i < payloadLen; i++) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Detector_IdentifiesByMagic() {
    var d = new JuiceFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("JuiceFs"));
    Assert.That(d.Extensions, Does.Contain(".juicefs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("JuiceFS"u8.ToArray()));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var d = new JuiceFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "juicefs-bundle.bin" }));
  }

  [Test, Category("Stub")]
  public void Description_FlagsDetectionOnly() {
    var d = new JuiceFsFormatDescriptor();
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("detection-only"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test, Category("Stub")]
  public void Description_DocumentsNoStandaloneImageFormat() {
    // Stage 0 is the honest treatment because JuiceFS has no standalone on-disk
    // image format — the Description must say so explicitly so consumers don't
    // mistakenly file R/O-promotion tickets.
    var d = new JuiceFsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("no standalone on-disk image format"));
    Assert.That(desc, Does.Contain("object storage"));
    Assert.That(desc, Does.Contain("metadata"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExposesRealBakMagicConstant() {
    // BakMagic 0x00747083 (juicedata/juicefs pkg/meta/backup.go) is the real
    // signature in the v1.3+ binary backup's EOS marker + protobuf footer.
    // Documenting it as a reachable constant prevents future confusion with
    // the wrapper-convention offset-0 "JuiceFS" tag.
    Assert.That(JuiceFsReader.BakMagic, Is.EqualTo(0x00747083u));
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_DocumentsRealMagicAndImpossibility() {
    var d = new JuiceFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal(payloadLen: 64));
    var entries = d.List(ms, password: null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);

    ms.Position = 0;
    using var r = new JuiceFsReader(ms);
    var iniEntry = r.Entries.First(e => e.Name == "metadata.ini");
    var iniText = Encoding.UTF8.GetString(iniEntry.Data);
    Assert.That(iniText, Does.Contain("0x00747083"), "metadata.ini must document the real BakMagic.");
    Assert.That(iniText, Does.Contain("ro_extraction_impossible_reason"),
      "metadata.ini must document why R/O extraction is structurally impossible.");
    Assert.That(iniText, Does.Contain("treatment=Stage 0 confirmed"));
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsImageMissingWrapperTag() {
    // Without the wrapper tag, the reader must refuse — exactly the boundary
    // case where a stray Redis dump / S3 chunk lacks any JuiceFS-shaped framing.
    var bogus = new byte[64];
    for (var i = 0; i < bogus.Length; i++) bogus[i] = (byte)i;
    using var ms = new MemoryStream(bogus);
    Assert.That(() => _ = new JuiceFsReader(ms), Throws.InstanceOf<InvalidDataException>());
  }
}
