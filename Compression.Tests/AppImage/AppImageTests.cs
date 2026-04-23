using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.AppImage;
using FileSystem.SquashFs;

namespace Compression.Tests.AppImage;

[TestFixture]
public class AppImageTests {

  /// <summary>
  /// Builds a minimal AppImage-shaped blob: a 64-byte ELF64 little-endian
  /// header marked with <c>AI\x02</c> at offset 8, with zero program/section
  /// headers, followed immediately by a SquashFS v4 image containing two files.
  /// </summary>
  private static byte[] BuildMinimalAppImage(byte typeByte = 0x02) {
    using var payload = new MemoryStream();
    using (var w = new SquashFsWriter(payload, leaveOpen: true)) {
      w.AddFile("AppRun", Encoding.UTF8.GetBytes("#!/bin/sh\nexec ./hello\n"));
      w.AddFile("hello.desktop", Encoding.UTF8.GetBytes("[Desktop Entry]\nName=Hello\n"));
    }
    var fs = payload.ToArray();

    // 64-byte ELF64 header, little-endian, EM_X86_64 (62), no program/section tables.
    var elf = new byte[64];
    elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
    elf[4] = 2; // EI_CLASS = ELFCLASS64
    elf[5] = 1; // EI_DATA  = ELFDATA2LSB
    elf[6] = 1; // EI_VERSION = EV_CURRENT
    elf[7] = 0; // EI_OSABI  = SYSV
    // AI marker at e_ident[8..10]
    elf[8] = (byte)'A';
    elf[9] = (byte)'I';
    elf[10] = typeByte;
    // Remaining EI_PAD bytes stay zero.

    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x10), 2);       // e_type = ET_EXEC
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x12), 62);      // e_machine = EM_X86_64
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x14), 1);       // e_version
    // e_entry, e_phoff, e_shoff all zero
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(0x30), 0);       // e_flags
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x34), 64);      // e_ehsize
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x36), 56);      // e_phentsize
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x38), 0);       // e_phnum
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x3A), 64);      // e_shentsize
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x3C), 0);       // e_shnum
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x3E), 0);       // e_shstrndx

    var result = new byte[elf.Length + fs.Length];
    elf.CopyTo(result, 0);
    fs.CopyTo(result, elf.Length);
    return result;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AreStable() {
    var d = new AppImageFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("AppImage"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".AppImage"));
    Assert.That(d.Extensions, Contains.Item(".AppImage"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    // Two magic entries (type-1 and type-2), both at offset 8.
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures.All(m => m.Offset == 8), Is.True);
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsMetadataPlusFilesystemEntries() {
    var data = BuildMinimalAppImage();
    using var ms = new MemoryStream(data);
    var entries = new AppImageFormatDescriptor().List(ms, null);

    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("metadata.ini"));
    Assert.That(entries.Any(e => e.Name.StartsWith("filesystem/")), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("AppRun")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_EmitsMetadataWithTypeAndArch() {
    var data = BuildMinimalAppImage();
    using var ms = new MemoryStream(data);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      new AppImageFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);
      var text = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(text, Does.Contain("type = 2"));
      Assert.That(text, Does.Contain("class = ELF64"));
      Assert.That(text, Does.Contain("endian = little"));
      Assert.That(text, Does.Contain("machine = x86_64"));
      Assert.That(text, Does.Contain("kind = SquashFS"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void List_Type1Marker_AlsoAccepted() {
    var data = BuildMinimalAppImage(typeByte: 0x01);
    using var ms = new MemoryStream(data);
    var entries = new AppImageFormatDescriptor().List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("EdgeCase")]
  public void List_NotAnElf_Throws() {
    using var ms = new MemoryStream(Enumerable.Repeat((byte)0x00, 1024).ToArray());
    Assert.That(() => new AppImageFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void List_ElfWithoutAppImageMarker_Throws() {
    var data = BuildMinimalAppImage();
    // Overwrite the 'AI' marker with zeros.
    data[8] = 0;
    data[9] = 0;
    data[10] = 0;
    using var ms = new MemoryStream(data);
    Assert.That(() => new AppImageFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
