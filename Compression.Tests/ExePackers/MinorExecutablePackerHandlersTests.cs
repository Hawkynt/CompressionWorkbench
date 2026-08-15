using Compression.Core.ExecutableUnpacking;
using FileFormat.ExePackers;
using NUnit.Framework;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using System.Buffers.Binary;
using Compression.Lib;
using Compression.Tests.Support;
using System.Linq;
using System;

namespace Compression.Tests.ExePackers;

[TestFixture]
public class MinorExecutablePackerHandlersTests {
  // Only handlers that use the generic MinorExecutablePackerHandlerBase.Unpack
  // path (locate section + generic aPLib/NRV decode + synthetic-PE rebuild).
  // Handlers that override Unpack — JDPack (its own container, see
  // JdpackExecutablePackerHandlerTests), Amber (reflective carve), the runtime
  // protectors (TELock/Themida/Yoda's Protector), MEW/NSPack/Yoda's Crypter/FSG
  // fallbacks — and the standalone validated unpackers (Eronana/hXOR) are
  // exercised by their own dedicated tests, not this generic-aPLib parametrized
  // case.
  private static readonly (string HandlerId, string Marker, string Section)[] MinorPackers = [
    ("alienyze", "Alienyze", ".alien"),
    ("beroexepacker", "BeRo", "bero"),
    ("exe32pack", "exe32pack", ".c"),
    ("expressor", "EXpressor", "ex_"),
    ("molebox", "Molebox", "mole"),
    ("neolite", "NeoLite", "neolit"),
    ("petite", "Petite", ".petite"),
    ("winupackfallback", "Upack", "Upack")
  ];

  [Test, Category("HappyPath")]
  public void MinorHandlers_CanDetectAndDecompressAplibPayload(
      [ValueSource(nameof(MinorPackers))] (string HandlerId, string Marker, string Section) packer) {
    var original = BuildOriginalImagePayload();
    var packed = BuildAplibPe(original, packer.Marker, packer.Section);

    var handler = ExecutablePackerHandlers.All.FirstOrDefault(h => h.Id == packer.HandlerId);
    if (handler == null) {
        handler = InstantiateHandler(packer.HandlerId);
    }
    
    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True, $"Failed to detect {packer.HandlerId}");

    var packedExe = handler.Parse(packed, detection);
    var result = handler.Unpack(packedExe, new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      var decompressed = result.Artifacts.SingleOrDefault(a => a.Name == "decompressed_payload.bin");
      Assert.That(decompressed, Is.Not.Null);
      Assert.That(decompressed!.Data, Is.EqualTo(original).AsCollection);
      Assert.That(result.Artifacts.Any(a => a.Name == "reconstructed/reconstructed.exe"), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void JdpackHandler_EmitsCompressedPayload_WhenSectionDoesNotDecode() {
    var payload = Enumerable.Range(0, 1024).Select(i => (byte)(i * 17)).ToArray();
    var packed = BuildRawPayloadPe(payload, "JDPack", ".jdpack");
    var handler = new JdpackExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void AmberHandler_EmitsReflectivePayloadSection_WhenSectionDoesNotDecode() {
    var payload = Enumerable.Range(0, 8192).Select(i => (byte)(255 - (i & 0xFF))).ToArray();
    var packed = BuildRawPayloadPe(payload, "amber", ".qDUUtb8");
    var handler = new AmberExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      // No plaintext embedded PE: Amber locates the largest payload-bearing region
      // as reflective_payload.bin and honestly reports it is obfuscated, not decoded.
      Assert.That(result.Artifacts.Single(a => a.Name == "reflective_payload.bin").Method, Is.EqualTo("amber-section"));
      Assert.That(result.Artifacts.Single(a => a.Name == "reflective_payload.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Exe32packHandler_EmitsCompressedPayload_WhenSectionDoesNotDecode() {
    var payload = Enumerable.Range(0, 2048).Select(i => (byte)(i ^ 0xA5)).ToArray();
    var packed = BuildRawPayloadPe(payload, "exe32pack", ".i");
    var handler = new Exe32packExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void FsgFallbackHandler_EmitsPayloadCandidate_FromStructuralSection() {
    var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
    var packed = BuildRawPayloadPe(payload, "FSG!", "ta");
    var handler = new FsgFallbackExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void TelockHandler_EmitsBlankEntrySectionPayload_WhenMarkerIsAbsent() {
    var payload = Enumerable.Range(0, 2048).Select(i => (byte)(i * 7)).ToArray();
    var packed = BuildTelockLikePe(payload);
    var handler = new TelockExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      // TELock is a runtime protector: it locates the protected body but never
      // fabricates a decompression (an empty section name sanitizes to "section").
      Assert.That(result.Artifacts.Single(a => a.Name == "protected_section_section.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  // hXOR-Packer's validated static unpacker (byte-exact rebuild) is covered in
  // StaticUnpackerTargetsTests; it keys off the DOS-header e_res2 insert offset,
  // not a trailing "FIFA" scan, so the old locate-only fixture no longer applies.

  [Test, Category("HappyPath")]
  public void SimpleDpackHandler_LocatesDpackSectionPayload() {
    var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i * 11)).ToArray();
    var packed = BuildRawPayloadPe(payload, "SimpleDpack", ".dpack");
    var handler = new SimpleDpackExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "dpack_section.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("ExternalTool")]
  public void ExternalPeToySource_DocumentedPetoyAplibLayout_RebuildsPayload() {
    var source = ExecutablePackerToolCache.GetPeToySource();
    Assume.That(source, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download the PE-Toy source archive.");
    Assert.Multiple(() => {
      Assert.That(File.Exists(Path.Combine(source!, "README.md")), Is.True);
      Assert.That(File.Exists(Path.Combine(source!, "shell.asm")), Is.True);
      Assert.That(File.Exists(Path.Combine(source!, "packer.cpp")), Is.True);
    });

    var original = BuildOriginalImagePayload();
    var packed = BuildAplibPe(original, marker: "", sectionName: ".petoy");
    var handler = new PeToyExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());
    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data,
        Is.EqualTo(original).AsCollection);
      Assert.That(result.Artifacts.Any(a => a.Name == "reconstructed/reconstructed.exe"), Is.True);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanRebuildExecutable), Is.True);
    });
  }

  [Test, Category("ExternalTool")]
  public void PackingBoxMewSample_RebuildsFromManagedPayloadRecovery() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assume.That(root, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download the Packing Box packed-PE corpus.");
    var sample = Path.Combine(root!, "MEW", "mew_accesschk.exe");
    Assume.That(File.Exists(sample), Is.True, "Packing Box MEW sample is not available.");

    var bytes = File.ReadAllBytes(sample);
    var match = ExecutablePackerHandlers.DetectBest(bytes);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("mew"));

    var result = match.Handler.Unpack(match.Handler.Parse(bytes, match.Detection), new());
    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data.Length, Is.GreaterThan(0));
      Assert.That(result.Artifacts.Any(a => a.Name == "reconstructed/reconstructed.exe"), Is.True);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanRebuildExecutable), Is.True);
    });
  }

  [Test, Category("ExternalTool")]
  public void PackingBoxPetiteSample_EmitsPetiteSectionPayload() {
    var root = ExecutablePackerToolCache.GetPackingBoxDatasetPackedRoot();
    Assume.That(root, Is.Not.Null, "Set CWB_DOWNLOAD_EXE_PACKER_TOOLS=1 to download the Packing Box packed-PE corpus.");
    var sample = Path.Combine(root!, "PEtite", "petite_7z.exe");
    Assume.That(File.Exists(sample), Is.True, "Packing Box PEtite sample is not available.");

    var bytes = File.ReadAllBytes(sample);
    var match = ExecutablePackerHandlers.DetectBest(bytes);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("petite"));

    var result = match.Handler.Unpack(match.Handler.Parse(bytes, match.Detection), new());
    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.bin").Data.Length, Is.GreaterThan(0));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void ThemidaHandler_EmitsBootSectionPayload_WhenSectionDoesNotDecode() {
    var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i * 13)).ToArray();
    var packed = BuildThemidaLikePe(payload);
    var handler = new ThemidaExecutablePackerHandler();

    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      // Themida is a runtime protector: the ".boot" body is located, never decoded.
      Assert.That(result.Artifacts.Single(a => a.Name == "protected_section_.boot.bin").Data, Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  private static IExecutablePackerHandler InstantiateHandler(string id) {
      return id switch {
          "alienyze" => new AlienyzeExecutablePackerHandler(),
          "amber" => new AmberExecutablePackerHandler(),
          "beroexepacker" => new BeRoExecutablePackerHandler(),
          "eronanapacker" => new EronanaExecutablePackerHandler(),
          "exe32pack" => new Exe32packExecutablePackerHandler(),
          "expressor" => new ExpressorExecutablePackerHandler(),
          "jdpack" => new JdpackExecutablePackerHandler(),
          "molebox" => new MoleboxExecutablePackerHandler(),
          "mew" => new MewExecutablePackerHandler(),
          "neolite" => new NeoliteExecutablePackerHandler(),
          "petite" => new PetiteExecutablePackerHandler(),
          "yodaprotector" => new YodaProtectorExecutablePackerHandler(),
          "themida" => new ThemidaExecutablePackerHandler(),
          "telock" => new TelockExecutablePackerHandler(),
          "winupackfallback" => new WinUpackFallbackExecutablePackerHandler(),
          "fsgfallback" => new FsgFallbackExecutablePackerHandler(),
          _ => throw new ArgumentException($"Unknown handler id: {id}")
      };
  }

  private static byte[] BuildOriginalImagePayload() {
    var buf = new byte[4096];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    for (var i = 2; i < buf.Length; i++) buf[i] = (byte)(i % 37);
    return buf;
  }

  private static byte[] BuildAplibPe(byte[] original, string marker, string sectionName) {
    var aplib = AplibBuildingBlock.CompressBare(original);
    return BuildPeWithSection(aplib, marker, sectionName, (uint)original.Length);
  }

  private static byte[] BuildRawPayloadPe(byte[] payload, string marker, string sectionName) =>
    BuildPeWithSection(payload, marker, sectionName, (uint)payload.Length);

  private static byte[] BuildTelockLikePe(byte[] payload) {
    var image = BuildPeWithSection(payload, "", "", (uint)payload.Length);
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 3);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), 0x3000);

    ".text"u8.CopyTo(image.AsSpan(sectionOffset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), 0);

    ".rsrc"u8.CopyTo(image.AsSpan(sectionOffset + 40, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 8), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 12), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 16), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 20), 0);

    image.AsSpan(sectionOffset + 80, 8).Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 80 + 8), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 80 + 12), 0x3000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 80 + 16), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 80 + 20), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 80 + 36), 0xE0000020);
    return image;
  }

  private static byte[] BuildThemidaLikePe(byte[] bootPayload) {
    var image = BuildPeWithSection(bootPayload, "", ".boot", (uint)bootPayload.Length);
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 2);
    ".themida"u8.CopyTo(image.AsSpan(sectionOffset + 40, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 8), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 12), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 16), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 20), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 36), 0xE0000060);
    return image;
  }

  private static byte[] BuildPeWithSection(byte[] sectionBytes, string marker, string sectionName, uint virtualSize) {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x200;

    var image = new byte[rawOffset + sectionBytes.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    if (!string.IsNullOrEmpty(marker)) {
        Encoding.ASCII.GetBytes(marker).CopyTo(image.AsSpan(0x40));
    }
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);

    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);      // x86
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);          // 1 section
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);    // PE32
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), 0x1000); // entry RVA
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000); // imagebase
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000); // section align
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);  // file align

    if (!string.IsNullOrEmpty(sectionName)) {
        var nameBytes = Encoding.ASCII.GetBytes(sectionName);
        nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(image.AsSpan(sectionOffset, 8));
    }
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), virtualSize); // vsize
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), 0x1000);   // vaddr
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), (uint)sectionBytes.Length); // raw size
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), rawOffset); // raw offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0xE0000020); // code+rwx
    sectionBytes.CopyTo(image.AsSpan(rawOffset));
    return image;
  }
}
