using System.Text;
using Compression.Registry;

namespace Compression.Tests.Gb;

[TestFixture]
public class GbTests {

  private static byte[] BuildMinimalRom(bool cgb = false) {
    // 32 KiB GB ROM with valid Nintendo logo + header.
    var rom = new byte[32768];
    // Place Nintendo logo at 0x0104..0x0133 (first 16 bytes are what we match).
    var logo = FileFormat.Gb.GbFormatDescriptor.NintendoLogoPrefix;
    Array.Copy(logo, 0, rom, 0x0104, logo.Length);
    // Title "HELLO" at 0x0134
    var title = "HELLO";
    for (var i = 0; i < title.Length; i++) rom[0x0134 + i] = (byte)title[i];
    rom[0x0143] = (byte)(cgb ? 0x80 : 0x00); // CGB flag
    rom[0x0147] = 0x00; // cart type ROM only
    rom[0x0148] = 0x00; // 32 KiB (2 banks)
    rom[0x0149] = 0x00; // no RAM
    rom[0x014A] = 0x01; // non-Japanese
    rom[0x014B] = 0x33; // new-licensee indicator
    rom[0x014C] = 0x00; // version
    // Header checksum: x = 0; for i in 0x134..0x14C: x = x - rom[i] - 1
    byte hc = 0;
    for (var i = 0x0134; i <= 0x014C; i++) hc = (byte)(hc - rom[i] - 1);
    rom[0x014D] = hc;
    return rom;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Gb.GbFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Gb"));
    Assert.That(d.Extensions, Does.Contain(".gb"));
    Assert.That(d.Extensions, Does.Contain(".gbc"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0x0104));
  }

  [Test, Category("HappyPath")]
  public void List_MinimalRom_ReturnsExpectedEntries() {
    var rom = BuildMinimalRom();
    var d = new FileFormat.Gb.GbFormatDescriptor();
    using var ms = new MemoryStream(rom);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("FULL.gb"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("header.bin"));
    Assert.That(names.Any(n => n.StartsWith("rom_banks/bank_")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesExpectedFiles() {
    var rom = BuildMinimalRom();
    var d = new FileFormat.Gb.GbFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      using var ms = new MemoryStream(rom);
      d.Extract(ms, tmpDir, null, null);
      Assert.That(File.Exists(Path.Combine(tmpDir, "FULL.gb")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "header.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmpDir, "header.bin")).Length, Is.EqualTo(0x50));
      Assert.That(File.Exists(Path.Combine(tmpDir, "rom_banks", "bank_000.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "rom_banks", "bank_001.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("header_checksum_ok=true"));
      Assert.That(meta, Does.Contain("title=HELLO"));
    } finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
  }

  [Test, Category("HappyPath")]
  public void CgbRom_EmitsGbcExtension() {
    var rom = BuildMinimalRom(cgb: true);
    var d = new FileFormat.Gb.GbFormatDescriptor();
    using var ms = new MemoryStream(rom);
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.gbc"), Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void List_TooSmall_DoesNotThrow() {
    var d = new FileFormat.Gb.GbFormatDescriptor();
    using var ms = new MemoryStream(new byte[100]);
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.gb"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }
}
