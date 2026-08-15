using System.Buffers.Binary;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Detection tests for Pakkero ELF launchers. Pakkero emits two build shapes —
/// fully stripped, and unstripped but with every section name blanked — and both
/// carry a large block of random padding past everything the ELF headers
/// describe. The samples below reproduce those two shapes, plus the near-miss
/// cases that must not match.
/// </summary>
[TestFixture]
public class PakkeroPackerTests {
  private const int PaddingLength = 300 * 1024;

  [Test, Category("HappyPath")]
  public void PakkeroHandler_DetectsStrippedLauncher() {
    var packed = BuildStrippedLauncher();
    Assert.That(new PakkeroExecutablePackerHandler().Detect(packed).IsMatch, Is.True);
  }

  [Test, Category("HappyPath")]
  public void PakkeroHandler_DetectsBlankSectionNameLauncher() {
    var packed = BuildSectionedLauncher(blankNames: true);
    Assert.That(new PakkeroExecutablePackerHandler().Detect(packed).IsMatch, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_SelectsPakkero() {
    var match = ExecutablePackerHandlers.DetectBest(BuildStrippedLauncher());
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("pakkero"));
  }

  [Test, Category("HappyPath")]
  public void PakkeroHandler_ReportsDetectionOnlyAndExplainsWhy() {
    var packed = BuildStrippedLauncher();
    var handler = new PakkeroExecutablePackerHandler();
    var detection = handler.Detect(packed);
    var result = handler.Unpack(handler.Parse(packed, detection), new());

    Assert.Multiple(() => {
      // Pakkero launchers embed no recoverable original, so nothing above
      // DetectionOnly may ever be claimed for them.
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.DetectionOnly));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.PayloadNotFound), Is.True);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanDecompressPayload), Is.False);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanRebuildExecutable), Is.False);
    });
  }

  [Test, Category("EdgeCase")]
  public void PakkeroHandler_RejectsNamedSectionsWithRandomTail() {
    // The shape of a different Go ELF crypter: high-entropy data appended to a
    // binary that still has ordinary section names.
    var packed = BuildSectionedLauncher(blankNames: false);
    Assert.That(new PakkeroExecutablePackerHandler().Detect(packed).IsMatch, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void PakkeroHandler_RejectsStrippedBinaryWithoutPadding() {
    var packed = BuildStrippedLauncher(paddingLength: 1024);
    Assert.That(new PakkeroExecutablePackerHandler().Detect(packed).IsMatch, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void PakkeroHandler_RejectsStrippedBinaryWithCompressiblePadding() {
    var packed = BuildStrippedLauncher(randomPadding: false);
    Assert.That(new PakkeroExecutablePackerHandler().Detect(packed).IsMatch, Is.False);
  }

  /// <summary>Fully stripped shape: no sections, and a writable segment with no file content.</summary>
  private static byte[] BuildStrippedLauncher(int paddingLength = PaddingLength, bool randomPadding = true) {
    const int phoff = 0x40;
    const int phentsize = 56;
    const int phnum = 3;
    const int codeEnd = 0x1000;

    var image = new byte[codeEnd + paddingLength];
    WriteElfHeader(image, phoff, phentsize, phnum, shoff: 0, shnum: 0, shstrndx: 0);

    var text = image.AsSpan(phoff);
    BinaryPrimitives.WriteUInt32LittleEndian(text, 1);              // PT_LOAD
    BinaryPrimitives.WriteUInt32LittleEndian(text[4..], 5);         // PF_R|PF_X
    BinaryPrimitives.WriteUInt64LittleEndian(text[8..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(text[16..], 0x400000);
    BinaryPrimitives.WriteUInt64LittleEndian(text[32..], codeEnd);
    BinaryPrimitives.WriteUInt64LittleEndian(text[40..], codeEnd);

    var data = image.AsSpan(phoff + phentsize);
    BinaryPrimitives.WriteUInt32LittleEndian(data, 1);              // PT_LOAD
    BinaryPrimitives.WriteUInt32LittleEndian(data[4..], 6);         // PF_R|PF_W
    BinaryPrimitives.WriteUInt64LittleEndian(data[8..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(data[16..], 0x4AD000);
    BinaryPrimitives.WriteUInt64LittleEndian(data[32..], 0);        // nothing in the file
    BinaryPrimitives.WriteUInt64LittleEndian(data[40..], 0x13C9E8); // all of it .bss

    var stack = image.AsSpan(phoff + 2 * phentsize);
    BinaryPrimitives.WriteUInt32LittleEndian(stack, 0x6474E551);    // PT_GNU_STACK
    BinaryPrimitives.WriteUInt32LittleEndian(stack[4..], 6);

    FillPadding(image.AsSpan(codeEnd), randomPadding, seed: 0x1E3779B9);
    return image;
  }

  /// <summary>
  /// Unstripped shape. With <paramref name="blankNames"/> the section name table
  /// holds only terminators, which is what Pakkero leaves behind; otherwise the
  /// sections keep ordinary names and the sample must not match.
  /// </summary>
  private static byte[] BuildSectionedLauncher(bool blankNames) {
    const int phoff = 0x40;
    const int phentsize = 56;
    const int phnum = 1;
    const int shentsize = 64;
    const int shnum = 2;
    const int codeEnd = 0x1000;

    var names = blankNames ? new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 } : "\0.text\0"u8.ToArray();
    var strOffset = codeEnd;
    var shoff = strOffset + names.Length;
    var describedEnd = shoff + shentsize * shnum;
    var image = new byte[describedEnd + PaddingLength];

    WriteElfHeader(image, phoff, phentsize, phnum, shoff, shnum, shstrndx: 1);

    var text = image.AsSpan(phoff);
    BinaryPrimitives.WriteUInt32LittleEndian(text, 1);              // PT_LOAD
    BinaryPrimitives.WriteUInt32LittleEndian(text[4..], 5);         // PF_R|PF_X
    BinaryPrimitives.WriteUInt64LittleEndian(text[16..], 0x400000);
    BinaryPrimitives.WriteUInt64LittleEndian(text[32..], codeEnd);
    BinaryPrimitives.WriteUInt64LittleEndian(text[40..], codeEnd);

    names.CopyTo(image.AsSpan(strOffset));

    // Section 0 is the mandatory null entry; section 1 is the name table itself.
    var code = image.AsSpan(shoff + shentsize);
    BinaryPrimitives.WriteUInt32LittleEndian(code, blankNames ? 0u : 1u); // sh_name
    BinaryPrimitives.WriteUInt32LittleEndian(code[4..], 3);               // SHT_STRTAB
    BinaryPrimitives.WriteUInt64LittleEndian(code[24..], (ulong)strOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(code[32..], (ulong)names.Length);

    FillPadding(image.AsSpan(describedEnd), random: true, seed: 0x05EBCA6B);
    return image;
  }

  private static void WriteElfHeader(byte[] image, int phoff, int phentsize, int phnum, int shoff, int shnum, int shstrndx) {
    image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
    image[4] = 2; image[5] = 1; image[6] = 1;                            // ELF64 LE v1
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 2);     // ET_EXEC
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);  // EM_X86_64
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x20), (ulong)phoff);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), (ulong)shoff);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), (ushort)phentsize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), (ushort)phnum);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), (ushort)shnum);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), (ushort)shstrndx);
  }

  private static void FillPadding(Span<byte> destination, bool random, int seed) {
    if (!random) {
      destination.Fill(0x41);
      return;
    }
    var rng = new Random(seed);
    var buffer = new byte[destination.Length];
    rng.NextBytes(buffer);
    buffer.CopyTo(destination);
  }
}
