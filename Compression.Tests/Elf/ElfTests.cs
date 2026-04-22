#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Elf;

namespace Compression.Tests.Elf;

[TestFixture]
public class ElfTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ElfFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Elf"));
    Assert.That(d.Extensions, Contains.Item(".elf"));
    Assert.That(d.Extensions, Contains.Item(".so"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures[0].Bytes[0], Is.EqualTo(0x7F));
  }

  [Test, Category("HappyPath")]
  public void SyntheticElf64_WithInterp_ProducesInterpEntry() {
    // Build a minimal little-endian 64-bit ELF with two sections:
    //   [0] SHT_NULL (required first entry)
    //   [1] .shstrtab (section-header string table)
    //   [2] .interp  ("fake-loader\0")
    // Layout: 64-byte ELF header, interp payload, shstrtab payload, section header table.
    var interpPayload = Encoding.ASCII.GetBytes("fake-loader\0");
    // shstrtab contains the null section name + ".shstrtab\0" + ".interp\0"
    var shstr = new MemoryStream();
    shstr.WriteByte(0);                                       // entry [0] ""
    var shstrNameOff = (int)shstr.Position;
    shstr.Write(Encoding.ASCII.GetBytes(".shstrtab\0"));
    var interpNameOff = (int)shstr.Position;
    shstr.Write(Encoding.ASCII.GetBytes(".interp\0"));
    var shstrBytes = shstr.ToArray();

    const int elfHdr = 64;
    var interpOff = elfHdr;
    var shstrOff = interpOff + interpPayload.Length;
    var shdrOff = shstrOff + shstrBytes.Length;
    const int shentsize = 64;
    const int shnum = 3;
    var totalLen = shdrOff + shnum * shentsize;
    var buf = new byte[totalLen];

    // e_ident
    buf[0] = 0x7F; buf[1] = (byte)'E'; buf[2] = (byte)'L'; buf[3] = (byte)'F';
    buf[4] = 2;  // EI_CLASS = ELFCLASS64
    buf[5] = 1;  // EI_DATA = little-endian
    buf[6] = 1;  // EI_VERSION
    // e_type(2) @16, e_machine(2) @18, e_version(4) @20
    // e_entry(8) @24, e_phoff(8) @32, e_shoff(8) @40
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x28), (ulong)shdrOff);
    // e_flags(4) @48, e_ehsize(2) @52, e_phentsize(2) @54, e_phnum(2) @56,
    // e_shentsize(2) @58, e_shnum(2) @60, e_shstrndx(2) @62
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x3A), shentsize);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x3C), shnum);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x3E), 1); // shstrndx = 1

    // Payloads.
    interpPayload.CopyTo(buf, interpOff);
    shstrBytes.CopyTo(buf, shstrOff);

    // Section headers (64 bytes each for ELF64): name(4) type(4) flags(8) addr(8)
    // offset(8) size(8) link(4) info(4) addralign(8) entsize(8)
    // [0] SHT_NULL — leave zeroed.
    // [1] .shstrtab, type=3 (SHT_STRTAB)
    var s1 = shdrOff + shentsize;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(s1 + 0), (uint)shstrNameOff);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(s1 + 4), 3);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s1 + 24), (ulong)shstrOff);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s1 + 32), (ulong)shstrBytes.Length);
    // [2] .interp, type=1 (SHT_PROGBITS)
    var s2 = shdrOff + 2 * shentsize;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(s2 + 0), (uint)interpNameOff);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(s2 + 4), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s2 + 24), (ulong)interpOff);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(s2 + 32), (ulong)interpPayload.Length);

    using var ms = new MemoryStream(buf);
    var entries = new ElfFormatDescriptor().List(ms, null);
    Assert.That(entries, Is.Not.Empty);
    Assert.That(entries.Any(e => e.Name == "interp.txt"), Is.True);
    Assert.That(entries.Any(e => e.Name == "sections/shstrtab.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void MalformedFile_ThrowsInvalidData() {
    // "ELFX" — wrong 4th magic byte.
    var buf = new byte[]{ 0x7F, (byte)'E', (byte)'L', (byte)'X', 0,0,0,0, 0,0,0,0, 0,0,0,0 };
    using var ms = new MemoryStream(buf);
    Assert.That(() => new ElfFormatDescriptor().List(ms, null), Throws.InstanceOf<InvalidDataException>());
  }
}
