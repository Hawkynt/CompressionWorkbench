using System.Text;
using Compression.Registry;

namespace Compression.Tests.Nes;

[TestFixture]
public class NesTests {

  private static byte[] BuildMinimalRom(int prg16k = 1, int chr8k = 1, bool trainer = false, int mapper = 0) {
    var prgSize = prg16k * 16384;
    var chrSize = chr8k * 8192;
    var trainerSize = trainer ? 512 : 0;
    var rom = new byte[16 + trainerSize + prgSize + chrSize];
    rom[0] = 0x4E; rom[1] = 0x45; rom[2] = 0x53; rom[3] = 0x1A;
    rom[4] = (byte)prg16k;
    rom[5] = (byte)chr8k;
    rom[6] = (byte)(((mapper & 0x0F) << 4) | (trainer ? 0x04 : 0));
    rom[7] = (byte)(mapper & 0xF0);
    return rom;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Nes.NesFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Nes"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".nes"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void List_MinimalRom_ReturnsExpectedEntries() {
    var rom = BuildMinimalRom();
    var d = new FileFormat.Nes.NesFormatDescriptor();
    using var ms = new MemoryStream(rom);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("FULL.nes"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("prg_rom.bin"));
    Assert.That(names, Does.Contain("chr_rom.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesExpectedFiles() {
    var rom = BuildMinimalRom(prg16k: 1, chr8k: 1);
    var d = new FileFormat.Nes.NesFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      using var ms = new MemoryStream(rom);
      d.Extract(ms, tmpDir, null, null);
      Assert.That(File.Exists(Path.Combine(tmpDir, "FULL.nes")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "prg_rom.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "chr_rom.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmpDir, "prg_rom.bin")).Length, Is.EqualTo(16384));
      Assert.That(new FileInfo(Path.Combine(tmpDir, "chr_rom.bin")).Length, Is.EqualTo(8192));
      var meta = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("prg_16kb_banks=1"));
      Assert.That(meta, Does.Contain("chr_8kb_banks=1"));
    } finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
  }

  [Test, Category("HappyPath")]
  public void Extract_WithTrainer_EmitsTrainerBin() {
    var rom = BuildMinimalRom(trainer: true);
    var d = new FileFormat.Nes.NesFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      using var ms = new MemoryStream(rom);
      d.Extract(ms, tmpDir, null, null);
      Assert.That(File.Exists(Path.Combine(tmpDir, "trainer.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmpDir, "trainer.bin")).Length, Is.EqualTo(512));
    } finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
  }

  [Test, Category("ErrorHandling")]
  public void List_InvalidMagic_DoesNotThrow_ReturnsPartial() {
    var bogus = new byte[256];
    var d = new FileFormat.Nes.NesFormatDescriptor();
    using var ms = new MemoryStream(bogus);
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.nes"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }
}
