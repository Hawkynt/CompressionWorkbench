using System.Text;
using FileFormat.Psf;

namespace Compression.Tests.Psf;

[TestFixture]
public class PsfTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_ProgramOnly() {
    var program = new byte[1024];
    for (var i = 0; i < program.Length; ++i)
      program[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new PsfWriter(ms, leaveOpen: true))
      w.ProgramData = program;
    ms.Position = 0;

    using var r = new PsfReader(ms);
    Assert.That(r.IsCorrupt, Is.False);
    Assert.That(r.ProgramData, Is.EqualTo(program));
    Assert.That(r.ReservedData, Is.Empty);
    Assert.That(r.Tags, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_WithReservedAndTags() {
    var program = "fake-game-program"u8.ToArray();
    var reserved = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34 };

    using var ms = new MemoryStream();
    using (var w = new PsfWriter(ms, leaveOpen: true)) {
      w.ProgramData = program;
      w.ReservedData = reserved;
      w.Tags["title"] = "Test";
      w.Tags["artist"] = "Foo";
    }
    ms.Position = 0;

    using var r = new PsfReader(ms);
    Assert.That(r.IsCorrupt, Is.False);
    Assert.That(r.ProgramData, Is.EqualTo(program));
    Assert.That(r.ReservedData, Is.EqualTo(reserved));
    Assert.That(r.Tags["title"], Is.EqualTo("Test"));
    Assert.That(r.Tags["artist"], Is.EqualTo("Foo"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_VersionByte() {
    var program = "ps2 program"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new PsfWriter(ms, leaveOpen: true)) {
      w.VersionByte = 0x02;
      w.ProgramData = program;
    }
    ms.Position = 0;

    using var r = new PsfReader(ms);
    Assert.That(r.VersionByte, Is.EqualTo((byte)0x02));
    Assert.That(r.ProgramData, Is.EqualTo(program));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_VerifiesCrcMismatch_DoesNotThrow() {
    // Hand-craft a valid PSF, then flip the CRC field so reader sees a mismatch.
    using var ms = new MemoryStream();
    using (var w = new PsfWriter(ms, leaveOpen: true))
      w.ProgramData = "payload"u8.ToArray();
    var bytes = ms.ToArray();

    // CRC32 lives at header offset 12..16; XOR a bit to corrupt without altering anything else.
    bytes[12] ^= 0xFF;

    using var corrupted = new MemoryStream(bytes);
    Assert.DoesNotThrow(() => {
      using var r = new PsfReader(corrupted);
      Assert.That(r.IsCorrupt, Is.True);
      Assert.That(r.ProgramData, Is.Not.Empty);
      Assert.That(r.Entries, Is.Not.Empty);
    });
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[PsfConstants.HeaderSize];
    Array.Fill(buf, (byte)0xAB);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new PsfReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesNoTagBlock() {
    using var ms = new MemoryStream();
    using (var w = new PsfWriter(ms, leaveOpen: true))
      w.ProgramData = "no-tags-here"u8.ToArray();
    ms.Position = 0;

    using var r = new PsfReader(ms);
    Assert.That(r.Tags, Is.Empty);
    Assert.That(r.Entries.Any(e => e.Name == PsfConstants.EntryTags), Is.False);
  }

  [Test, Category("HappyPath")]
  public void List_HasRightSyntheticEntries() {
    using var ms = new MemoryStream();
    using (var w = new PsfWriter(ms, leaveOpen: true)) {
      w.ProgramData = "abc"u8.ToArray();
      w.Tags["title"] = "Demo";
    }
    ms.Position = 0;

    using var r = new PsfReader(ms);
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EqualTo(new[] {
      PsfConstants.EntryHeader,
      PsfConstants.EntryProgram,
      PsfConstants.EntryTags,
    }));
  }

  [Test, Category("HappyPath")]
  public void Crc32_KnownVector() {
    // Standard CRC-32 (IEEE 802.3) check vector locks the polynomial direction (reflected, XOR-out 0xFFFFFFFF).
    var crc = PsfCrc32.Compute(Encoding.UTF8.GetBytes("123456789"));
    Assert.That(crc, Is.EqualTo(0xCBF43926u));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new PsfFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Psf"));
    Assert.That(d.DisplayName, Is.EqualTo("Portable Sound Format"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".psf"));
    Assert.That(d.Extensions, Contains.Item(".psf"));
    Assert.That(d.Extensions, Contains.Item(".minipsf"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("PSF"u8.ToArray()));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("psf-zlib"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("PSF zlib"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }
}
