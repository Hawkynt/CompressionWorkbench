using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Snes;

[TestFixture]
public class SnesTests {

  private static byte[] BuildMinimalLoRom() {
    // 32 KiB LoROM (smallest typical): header at 0x7FC0.
    var rom = new byte[32768];
    // Populate 21-byte title "TEST TITLE" padded with spaces.
    var title = "TEST TITLE";
    for (var i = 0; i < title.Length; i++) rom[0x7FC0 + i] = (byte)title[i];
    for (var i = title.Length; i < 21; i++) rom[0x7FC0 + i] = 0x20;
    rom[0x7FC0 + 0x15] = 0x20; // map mode LoROM
    rom[0x7FC0 + 0x16] = 0x00; // cart type ROM only
    rom[0x7FC0 + 0x17] = 0x08; // ROM size
    rom[0x7FC0 + 0x18] = 0x00; // SRAM
    rom[0x7FC0 + 0x19] = 0x01; // region USA
    rom[0x7FC0 + 0x1A] = 0x33; // dev
    rom[0x7FC0 + 0x1B] = 0x00; // version
    // Checksum complement + checksum: any pair xor = 0xFFFF.
    BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x7FC0 + 0x1C, 2), 0x1234);
    BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x7FC0 + 0x1E, 2), unchecked((ushort)~0x1234));
    return rom;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Snes.SnesFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Snes"));
    Assert.That(d.Extensions, Does.Contain(".sfc"));
    Assert.That(d.Extensions, Does.Contain(".smc"));
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_LoRom_ReturnsExpectedEntries() {
    var rom = BuildMinimalLoRom();
    var d = new FileFormat.Snes.SnesFormatDescriptor();
    using var ms = new MemoryStream(rom);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("FULL.sfc"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("rom.bin"));
    Assert.That(names, Does.Contain("internal_header.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_LoRom_WritesFiles() {
    var rom = BuildMinimalLoRom();
    var d = new FileFormat.Snes.SnesFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      using var ms = new MemoryStream(rom);
      d.Extract(ms, tmpDir, null, null);
      Assert.That(File.Exists(Path.Combine(tmpDir, "FULL.sfc")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "rom.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "internal_header.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmpDir, "internal_header.bin")).Length, Is.EqualTo(0x40));
      var meta = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("layout=LoROM"));
      Assert.That(meta, Does.Contain("checksum_valid=true"));
      Assert.That(meta, Does.Contain("title=TEST TITLE"));
    } finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
  }

  [Test, Category("HappyPath")]
  public void SmcHeaderDetection_512BytePrefix() {
    var rom = BuildMinimalLoRom();
    var withSmc = new byte[512 + rom.Length];
    Array.Copy(rom, 0, withSmc, 512, rom.Length);
    var d = new FileFormat.Snes.SnesFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      using var ms = new MemoryStream(withSmc);
      d.Extract(ms, tmpDir, null, null);
      Assert.That(File.Exists(Path.Combine(tmpDir, "smc_header.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmpDir, "smc_header.bin")).Length, Is.EqualTo(512));
      var meta = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("has_smc_header=true"));
    } finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
  }

  [Test, Category("ErrorHandling")]
  public void List_TooSmall_DoesNotThrow() {
    var d = new FileFormat.Snes.SnesFormatDescriptor();
    using var ms = new MemoryStream(new byte[100]);
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.sfc"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }
}
