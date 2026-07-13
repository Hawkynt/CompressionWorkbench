#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using Compression.Tests.Support;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// squishy (Jake "ferris" Taylor / logicoma, <c>https://logicoma.io/squishy</c>) detection and
/// honest-locate tests. The signature asserted here — a single PE section literally named
/// <c>logicoma</c>, plus the same "logicoma" text (and, from 0.2.0, a "squished by" credit
/// banner) embedded in the DOS-stub region ahead of squishy's own tiny <c>e_lfanew</c> — was
/// confirmed by packing hand-built minimal PEs with the official squishy-0.1.3 (x86) and
/// squishy-0.2.0 (x86-64) release binaries and hex-dumping the result.
/// </summary>
[TestFixture]
public class SquishyTests {
  private const int PeOffset = 0x80;
  private const int OptionalHeaderSize = 0xE0;
  private const int SectionTableOffset = PeOffset + 24 + OptionalHeaderSize;

  [Test, Category("HappyPath")]
  public void Squishy_DetectsSectionName() {
    var image = BuildPeWithSection("logicoma", headerLiteral: null, payload: [1, 2, 3, 4]);
    var handler = new SquishyExecutablePackerHandler();

    var detection = handler.Detect(image);

    Assert.Multiple(() => {
      Assert.That(detection.IsMatch, Is.True);
      Assert.That(detection.PackerId, Is.EqualTo("squishy"));
    });
  }

  [Test, Category("HappyPath")]
  public void Squishy_DetectsHeaderLogicomaLiteral_WithoutMatchingSectionName() {
    var image = BuildPeWithSection(".text", headerLiteral: "logicoma", payload: [1, 2, 3, 4]);
    var handler = new SquishyExecutablePackerHandler();

    Assert.That(handler.Detect(image).IsMatch, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Squishy_DetectsSquishedByBanner_WithoutMatchingSectionName() {
    var image = BuildPeWithSection(".text", headerLiteral: "squished by ferris@logicoma", payload: [1, 2, 3, 4]);
    var handler = new SquishyExecutablePackerHandler();

    Assert.That(handler.Detect(image).IsMatch, Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Squishy_PlainPe_DoesNotMatch() {
    var image = BuildPeWithSection(".text", headerLiteral: null, payload: [1, 2, 3, 4]);
    var handler = new SquishyExecutablePackerHandler();

    var detection = handler.Detect(image);

    Assert.Multiple(() => {
      Assert.That(detection.IsMatch, Is.False);
      Assert.That(detection.Diagnostics.Any(d => d.IsError), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void Squishy_LiteralOutsideHeaderWindow_DoesNotFalsePositive() {
    // "logicoma" placed well past the bounded 0x400 header-scan window, with a
    // non-matching section name: this must NOT be mistaken for squishy output,
    // since arbitrary unrelated files can legitimately contain that word in their
    // body (e.g. an intro's own credits text).
    var payload = new byte[0x800];
    Encoding.ASCII.GetBytes("logicoma").CopyTo(payload.AsSpan(0x600));
    var image = BuildPeWithSection(".text", headerLiteral: null, payload: payload);
    var handler = new SquishyExecutablePackerHandler();

    Assert.That(handler.Detect(image).IsMatch, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Squishy_Unpack_LocatesPayloadSection_WithHonestDiagnostic() {
    var payload = Enumerable.Range(0, 2048).Select(i => (byte)(i * 41)).ToArray();
    var image = BuildPeWithSection("logicoma", headerLiteral: null, payload: payload);
    var handler = new SquishyExecutablePackerHandler();

    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(image, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanLocatePayload), Is.True);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanDecompressPayload), Is.False);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanRebuildExecutable), Is.False);
      var diagnostic = result.Diagnostics.Single(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod);
      Assert.That(diagnostic.Message, Does.Contain("closed demoscene compressor"));
      Assert.That(diagnostic.Message, Does.Contain("runtime depacker"));
    });
  }

  [Test, Category("HappyPath")]
  public void Squishy_Unpack_NoPackerSectionPresent_StaysAtDetectionOnly() {
    // Header-literal-only detection with no "logicoma"-named section: nothing to
    // carve, so Unpack must honestly stay at DetectionOnly rather than fabricate
    // a payload location.
    var image = BuildPeWithSection(".text", headerLiteral: "logicoma", payload: [9, 9, 9]);
    var handler = new SquishyExecutablePackerHandler();

    var detection = handler.Detect(image);
    var result = handler.Unpack(handler.Parse(image, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.DetectionOnly));
      Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void Squishy_IsRegisteredExactlyOnce() {
    Assert.That(ExecutablePackerHandlers.All.Count(h => h.Id == "squishy"), Is.EqualTo(1));
  }

  [Test, Category("ExternalTool"), Explicit("Downloads/runs squishy only when external tool verification is requested.")]
  public void ExternalSquishyTool_PacksMinimalPe_AndOurHandlerLocatesTheRealPayloadSection() {
    var squishy = ExecutablePackerToolCache.GetSquishy();
    Assume.That(squishy, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download the official squishy release, or put squishy-x64 on PATH.");
    Assume.That(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), Is.True, "squishy is a Windows PE tool.");

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      var input = Path.Combine(tmp, "minimal.exe");
      File.WriteAllBytes(input, BuildMinimalRunnableX64Pe());
      var output = Path.Combine(tmp, "minimal-packed.exe");

      ExecutablePackerToolCache.Run(squishy!, "-i", input, "-o", output, "-p", "silent");
      Assume.That(File.Exists(output), Is.True, "squishy did not produce an output file for the minimal PE fixture.");

      var packedBytes = File.ReadAllBytes(output);
      var handler = new SquishyExecutablePackerHandler();
      var detection = handler.Detect(packedBytes);
      Assert.That(detection.IsMatch, Is.True, "squishy handler must detect real squishy output");

      var result = handler.Unpack(handler.Parse(packedBytes, detection), new UnpackOptions());
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  /// <summary>
  /// A hand-built, single-section, resource-free x64 PE that imports and calls
  /// <c>KERNEL32.dll!ExitProcess</c>. squishy panics on unrecognized resource types
  /// (confirmed against real Windows system executables, which normally carry a
  /// VERSIONINFO/RT_VERSION resource), so a from-scratch minimal PE is required to
  /// get past its own input sanity checks.
  /// </summary>
  private static byte[] BuildMinimalRunnableX64Pe() {
    const ulong imageBase = 0x140000000;
    const uint sectionRva = 0x1000;
    const uint sectionAlign = 0x1000;
    const uint fileAlign = 0x200;

    const int codeOff = 0x000;
    const int importDirOff = 0x040;
    const int iltOff = 0x080;
    const int iatOff = 0x090;
    const int hintNameOff = 0x0A0;
    const int dllNameOff = 0x0C0;

    var iatRva = sectionRva + iatOff;
    var sec = new byte[fileAlign];

    // sub rsp, 0x28 ; xor ecx, ecx ; call qword ptr [rip+disp] -> ExitProcess IAT slot ; int3
    var code = new List<byte> { 0x48, 0x83, 0xEC, 0x28, 0x31, 0xC9 };
    var callInstrRva = sectionRva + codeOff + (uint)code.Count;
    var nextInstrRva = callInstrRva + 6;
    var disp = (int)(iatRva - nextInstrRva);
    code.Add(0xFF); code.Add(0x15);
    code.AddRange(BitConverter.GetBytes(disp));
    code.Add(0xCC);
    code.ToArray().CopyTo(sec.AsSpan(codeOff));

    var dllNameRva = sectionRva + dllNameOff;
    var iltRva = sectionRva + iltOff;
    var hintNameRva = sectionRva + hintNameOff;

    BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(importDirOff + 0), iltRva);
    BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(importDirOff + 12), dllNameRva);
    BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(importDirOff + 16), iatRva);

    BinaryPrimitives.WriteUInt64LittleEndian(sec.AsSpan(iltOff), hintNameRva);
    BinaryPrimitives.WriteUInt64LittleEndian(sec.AsSpan(iatOff), hintNameRva);

    var hintName = new byte[2].Concat(Encoding.ASCII.GetBytes("ExitProcess\0")).ToArray();
    hintName.CopyTo(sec.AsSpan(hintNameOff));
    Encoding.ASCII.GetBytes("KERNEL32.dll\0").CopyTo(sec.AsSpan(dllNameOff));

    var sectionVirtualSize = (uint)(dllNameOff + "KERNEL32.dll\0".Length);
    var imageSize = Align(sectionRva + sectionVirtualSize, sectionAlign);
    var entryRva = sectionRva + codeOff;
    var importTableRva = sectionRva + importDirOff;

    var headersSize = Align((uint)(PeOffset + 4 + 20 + 0xF0 + 40), fileAlign);
    var image = new byte[headersSize + sec.Length];

    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), PeOffset);
    image[PeOffset] = (byte)'P'; image[PeOffset + 1] = (byte)'E';

    var coff = PeOffset + 4;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff), 0x8664);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16), 0xF0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 18), 0x0022);

    var opt = coff + 20;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt), 0x20B);
    image[opt + 2] = 14;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 4), (uint)sec.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 16), entryRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 20), sectionRva);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(opt + 24), imageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32), sectionAlign);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36), fileAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 40), 6);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 48), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 56), imageSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60), headersSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 68), 3);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(opt + 72), 0x100000);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(opt + 80), 0x1000);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(opt + 88), 0x100000);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(opt + 96), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 108), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 112 + 8), importTableRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 112 + 12), 40);

    var sectionHeader = opt + 0xF0;
    Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(image.AsSpan(sectionHeader, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 8), sectionVirtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 12), sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 16), (uint)sec.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 20), headersSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 36), 0xE0000060);

    sec.CopyTo(image.AsSpan((int)headersSize));
    return image;
  }

  private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

  /// <summary>
  /// A single-section PE32 fixture: <paramref name="sectionName"/> names the section,
  /// <paramref name="headerLiteral"/> (when supplied) is written into the DOS-stub area at
  /// offset 0x40 (well within squishy's confirmed 0x400 header-literal-scan window), and
  /// <paramref name="payload"/> becomes the section's raw data.
  /// </summary>
  private static byte[] BuildPeWithSection(string sectionName, string? headerLiteral, byte[] payload) {
    const int rawOffset = 0x200;
    var image = new byte[rawOffset + payload.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    if (headerLiteral != null)
      Encoding.ASCII.GetBytes(headerLiteral).CopyTo(image.AsSpan(0x40));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), PeOffset);

    "PE\0\0"u8.CopyTo(image.AsSpan(PeOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 6), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 20), OptionalHeaderSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 24), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(PeOffset + 24 + 16), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(PeOffset + 24 + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(PeOffset + 24 + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(PeOffset + 24 + 36), 0x200);

    var nameBytes = Encoding.ASCII.GetBytes(sectionName);
    nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(image.AsSpan(SectionTableOffset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 8), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 16), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 36), 0xE0000020);
    payload.CopyTo(image.AsSpan(rawOffset));
    return image;
  }
}
