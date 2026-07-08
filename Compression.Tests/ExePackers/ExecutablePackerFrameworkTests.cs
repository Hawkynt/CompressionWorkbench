using System.Buffers.Binary;
using System.Text;
using Compression.Core.Crypto;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.ExecutableUnpacking;
using Compression.Core.Streams;
using Compression.Lib;
using FileFormat.Bzip2;
using FileFormat.ExePackers;
using FileFormat.Gzip;
using FileFormat.Xz;
using FileFormat.Zstd;
using FileFormat.Upx;

namespace Compression.Tests.ExePackers;

[TestFixture]
public class ExecutablePackerFrameworkTests {
  [Test, Category("HappyPath")]
  public void RegisteredExecutablePackerHandlers_DoNotAdvertiseDetectionOnlyAsUnpacking() {
    var handlers = ExecutablePackerHandlers.All;

    const ExecutableUnpackCapabilities realUnpacking =
      ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload |
      ExecutableUnpackCapabilities.CanBuildMemoryImage |
      ExecutableUnpackCapabilities.CanRebuildExecutable |
      ExecutableUnpackCapabilities.CanProduceRunnableExecutable;

    foreach (var handler in handlers)
      Assert.That(handler.Capabilities & realUnpacking, Is.Not.EqualTo(ExecutableUnpackCapabilities.None), handler.Id);
  }

  [Test, Category("HappyPath")]
  public void Registry_ContainsCurrentRealExecutableUnpackers() {
    var ids = ExecutablePackerHandlers.All.Select(h => h.Id).ToArray();
    Assert.That(ids, Is.SupersetOf(new[] {
      "upx", "fsg", "aspack", "pecompact", "rlpack",
      "gzexe", "bzexe", "papaw", "gopacker", "origami", "silent_packer", "huan",
      "nrv_pe",
    }));
  }

  [Test, Category("HappyPath")]
  public void GzexeHandler_UnpacksWrapperToReconstructedExecutable() {
    var original = "#!/bin/sh\necho gzexe handler\n"u8.ToArray();
    var wrapper = BuildWrapper(original, "gzip -cd \"$0\"\n", compressed => {
      using var gzip = new GzipStream(compressed, CompressionStreamMode.Compress, leaveOpen: true);
      gzip.Write(original);
    });

    var handler = new GzexeExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_UnpacksGzexeWrapper() {
    var original = "#!/bin/sh\necho registry gzexe\n"u8.ToArray();
    var wrapper = BuildWrapper(original, "gzip -cd \"$0\"\n", compressed => {
      using var gzip = new GzipStream(compressed, CompressionStreamMode.Compress, leaveOpen: true);
      gzip.Write(original);
    });

    var match = ExecutablePackerHandlers.DetectBest(wrapper);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("gzexe"));

    var result = ExecutablePackerHandlers.TryUnpack(wrapper);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
      Is.EqualTo(original).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void BzexeHandler_UnpacksWrapperToReconstructedExecutable() {
    var original = "#!/bin/sh\necho bzexe handler\n"u8.ToArray();
    var wrapper = BuildWrapper(original, "bzip2 -cd \"$0\"\n", compressed => {
      using var bzip2 = new Bzip2Stream(compressed, CompressionStreamMode.Compress, leaveOpen: true);
      bzip2.Write(original);
    });

    var handler = new BzexeExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void PapawHandler_UnpacksWrapperToReconstructedExecutable() {
    var original = "#!/bin/sh\necho papaw handler\n"u8.ToArray();
    var wrapper = BuildPapawWrapper(original);

    var handler = new PapawExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.restored.xz").Data.AsSpan(0, 5).ToArray(),
        Is.EqualTo(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A }).AsCollection);
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void GoPackerHandler_UnpacksWrapperToReconstructedExecutable() {
    var original = "#!/bin/sh\necho gopacker handler\n"u8.ToArray();
    var wrapper = BuildGoPackerWrapper(original);

    var handler = new GoPackerExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.zst").Data.AsSpan(0, 4).ToArray(),
        Is.EqualTo(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }).AsCollection);
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void OrigamiHandler_UnpacksWrapperToReconstructedAssembly() {
    var original = "MZ origami handler assembly"u8.ToArray();
    var wrapper = BuildOrigamiWrapper(original);

    var handler = new OrigamiExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.deflate").Data.Length, Is.GreaterThan(0));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/original_assembly.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void SilentPackerHandler_UnpacksElf64XorSectionInsertion() {
    var originalText = "framework silent text"u8.ToArray();
    var wrapper = BuildSilentPackerElf64(originalText);

    var handler = new SilentPackerExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decrypted_text.bin").Data,
        Is.EqualTo(originalText).AsCollection);
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.elf").Data.AsSpan(0x100, originalText.Length).ToArray(),
        Is.EqualTo(originalText).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void HuanHandler_UnpacksEmbeddedPePayload() {
    var original = MinimalPe();
    var wrapper = BuildHuanWrapper(original);

    var handler = new HuanExecutablePackerHandler();
    var result = Unpack(handler, wrapper);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.exe").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  private static readonly (string HandlerId, string Marker, string Section)[] AplibPackers = [
    ("fsg", "FSG!", ".fsg"),
    ("aspack", "ASPack", ".aspack"),
    ("pecompact", "PEC2", ".pec1"),
    ("rlpack", "RLPack", ".RLPack"),
  ];

  [Test, Category("HappyPath")]
  public void AplibPackerHandlers_DecompressAplibSectionPayload(
      [ValueSource(nameof(AplibPackers))] (string HandlerId, string Marker, string Section) packer) {
    var original = BuildOriginalImagePayload();
    var packed = BuildAplibPe(original, packer.Marker, packer.Section);

    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == packer.HandlerId);
    var result = Unpack(handler, packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data,
        Is.EqualTo(original).AsCollection);
      Assert.That(result.Artifacts.Any(a => a.Name == "reconstructed/reconstructed.exe"), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_UnpacksAplibPe(
      [ValueSource(nameof(AplibPackers))] (string HandlerId, string Marker, string Section) packer) {
    var original = BuildOriginalImagePayload();
    var packed = BuildAplibPe(original, packer.Marker, packer.Section);

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo(packer.HandlerId));

    var result = ExecutablePackerHandlers.TryUnpack(packed);
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data,
      Is.EqualTo(original).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void GenericAplibHandler_DecodesUnmarkedAplibPe() {
    var original = BuildOriginalImagePayload();
    // No recognized packer marker and a neutral ".text" section: only the generic
    // aPLib fallback should match, purely by successfully inflating the section.
    var packed = BuildAplibPe(original, marker: "", sectionName: ".text");

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("aplib_pe"));

    var result = ExecutablePackerHandlers.TryUnpack(packed);
    Assert.That(result!.Level, Is.GreaterThanOrEqualTo(ExecutableUnpackLevel.PayloadDecompressed));
    Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data,
      Is.EqualTo(original).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void AplibPackerHandler_EmitsLocatedPayload_WhenCodecDoesNotDecode() {
    var packed = BuildAplibLikePeWithRawPayload("RLPack", ".RLPack");

    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "rlpack");
    var result = Unpack(handler, packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True);
      Assert.That(result.Artifacts.Any(a => a.Name == "decompressed_payload.bin"), Is.False);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.DecompressionFailed), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void WinUpackHandler_LocatesPackedPayload() {
    var packed = BuildWinUpackLikePe();

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("winupack"));

    var result = ExecutablePackerHandlers.TryUnpack(packed);
    Assert.Multiple(() => {
      Assert.That(result, Is.Not.Null);
      Assert.That(result!.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "compressed_payload.bin").Data.Length, Is.EqualTo(0x1800));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void MPressHandler_LocatesMpressSectionsAsPayloadCandidates() {
    var packed = BuildMPressLikePe();

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("mpress"));

    var result = ExecutablePackerHandlers.TryUnpack(packed);
    Assert.Multiple(() => {
      Assert.That(result, Is.Not.Null);
      Assert.That(result!.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "payload_candidates/candidate_000_.MPRESS1.bin"), Is.True);
      Assert.That(result.Artifacts.Any(a => a.Name == "payload_candidates/candidate_001_.MPRESS2.bin"), Is.True);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void GenericNrvHandler_DecodesBareNrvPeSectionPayload() {
    var original = BuildOriginalImagePayload();
    var packed = BuildNrvPe(original);

    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "nrv_pe");
    var result = Unpack(handler, packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data,
        Is.EqualTo(original).AsCollection);
      Assert.That(result.Artifacts.Any(a => a.Name == "reconstructed/reconstructed.exe"), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_UnpacksUnmarkedNrvPeAfterSpecificHandlers() {
    var original = BuildOriginalImagePayload();
    var packed = BuildNrvPe(original);

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("nrv_pe"));

    var result = ExecutablePackerHandlers.TryUnpack(packed);
    Assert.That(result!.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data,
      Is.EqualTo(original).AsCollection);
  }

  private static byte[] BuildOriginalImagePayload() {
    // A compressible original image (a small MZ blob padded with structured
    // content) so the aPLib body meaningfully expands, as a real FSG payload does.
    var buf = new byte[4096];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    for (var i = 2; i < buf.Length; i++) buf[i] = (byte)(i % 37);
    return buf;
  }

  private static byte[] BuildAplibPe(byte[] original, string marker, string sectionName) {
    var aplib = AplibBuildingBlock.CompressBare(original);

    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x200;

    var image = new byte[rawOffset + aplib.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    // The stub embeds the packer marker near the entry; place it in the DOS stub.
    Encoding.ASCII.GetBytes(marker).CopyTo(image.AsSpan(0x40));
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

    var nameBytes = Encoding.ASCII.GetBytes(sectionName);
    nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(image.AsSpan(sectionOffset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), (uint)original.Length); // vsize
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), 0x1000);   // vaddr
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), (uint)aplib.Length); // raw size
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), rawOffset); // raw offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0xE0000020); // code+rwx
    aplib.CopyTo(image.AsSpan(rawOffset));
    return image;
  }

  private static byte[] BuildAplibLikePeWithRawPayload(string marker, string sectionName) {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x400;
    var payload = new byte[0x1000];
    new Random(0x5151).NextBytes(payload);

    var image = new byte[rawOffset + payload.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    Encoding.ASCII.GetBytes(marker).CopyTo(image.AsSpan(0x40));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);

    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), 0x3000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    ".packed\0"u8.CopyTo(image.AsSpan(sectionOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), 0x8000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0x60000020);

    var nameBytes = Encoding.ASCII.GetBytes(sectionName);
    nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(image.AsSpan(sectionOffset + 40, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 8), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 12), 0x9000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 16), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 36), 0xE0000020);
    payload.CopyTo(image.AsSpan(rawOffset));
    return image;
  }

  private static byte[] BuildWinUpackLikePe() {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x200;
    var payload = new byte[0x1800];
    new Random(0xA11).NextBytes(payload);

    var image = new byte[rawOffset + payload.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), 0x9000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    ".Upack\0\0"u8.CopyTo(image.AsSpan(sectionOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), 0x6000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0xE0000060);

    ".rsrc\0\0\0"u8.CopyTo(image.AsSpan(sectionOffset + 40));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 8), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 12), 0x8000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 16), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 40 + 36), 0xE0000060);
    payload.CopyTo(image.AsSpan(rawOffset));
    return image;
  }

  private static byte[] BuildMPressLikePe() {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int raw1 = 0x400;
    const int raw2 = 0x1800;
    var payload1 = new byte[0x1400];
    var payload2 = new byte[0x400];
    new Random(0x4D).NextBytes(payload1);
    new Random(0x50).NextBytes(payload2);

    var image = new byte[raw2 + payload2.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    Encoding.ASCII.GetBytes("MPRESS").CopyTo(image.AsSpan(0x40));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), 0x7000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    WriteSection(image, sectionOffset, ".MPRESS1", 0x6000, 0x1000, payload1.Length, raw1, 0xE00000E0);
    WriteSection(image, sectionOffset + 40, ".MPRESS2", 0x1000, 0x7000, payload2.Length, raw2, 0xE00000E0);
    payload1.CopyTo(image.AsSpan(raw1));
    payload2.CopyTo(image.AsSpan(raw2));
    return image;
  }

  private static void WriteSection(byte[] image, int offset, string name, uint virtualSize, uint virtualAddress,
      int rawSize, int rawOffset, uint flags) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(image.AsSpan(offset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 8), virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 12), virtualAddress);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 16), (uint)rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 20), (uint)rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 36), flags);
  }

  private static byte[] BuildNrvPe(byte[] original) {
    var nrv = Nrv2bBuildingBlock.CompressBare(original, refillWidthBytes: 4);

    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x200;

    var image = new byte[rawOffset + nrv.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);

    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    ".nrv\0\0\0\0"u8.CopyTo(image.AsSpan(sectionOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), (uint)original.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), (uint)nrv.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0xE0000020);
    nrv.CopyTo(image.AsSpan(rawOffset));
    return image;
  }

  private static UnpackResult Unpack(IExecutablePackerHandler handler, byte[] image) {
    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);
    return handler.Unpack(handler.Parse(image, detection), new());
  }

  private static byte[] BuildWrapper(byte[] original, string marker, Action<MemoryStream> compress) {
    using var compressed = new MemoryStream();
    compress(compressed);
    var header = System.Text.Encoding.ASCII.GetBytes("#!/bin/sh\n" + marker);
    var result = new byte[header.Length + compressed.Length];
    header.CopyTo(result.AsSpan());
    compressed.ToArray().CopyTo(result.AsSpan(header.Length));
    return result;
  }

  private static byte[] BuildPapawWrapper(byte[] original) {
    var stub = new byte[0x200];
    stub[0] = 0x7F; stub[1] = (byte)'E'; stub[2] = (byte)'L'; stub[3] = (byte)'F';
    stub[4] = 2; stub[5] = 1;

    using var compressed = new MemoryStream();
    using (var xz = new XzStream(compressed, CompressionStreamMode.Compress, dictionarySize: 512 * 1024, checkType: 0, leaveOpen: true))
      xz.Write(original);
    var fullXz = compressed.ToArray();
    var obfuscated = fullXz.ToArray();
    obfuscated[0] = 0; obfuscated[1] = 0; obfuscated[2] = 0; obfuscated[3] = 0x08; obfuscated[4] = 0;
    obfuscated[^2] = 0; obfuscated[^1] = 0;

    var result = new byte[stub.Length + obfuscated.Length + 8];
    stub.CopyTo(result.AsSpan());
    obfuscated.CopyTo(result.AsSpan(stub.Length));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 8), (uint)original.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), (uint)obfuscated.Length);
    return result;
  }

  private static byte[] BuildGoPackerWrapper(byte[] original) {
    var stub = new byte[0x200];
    stub[0] = 0x7F; stub[1] = (byte)'E'; stub[2] = (byte)'L'; stub[3] = (byte)'F';
    stub[4] = 2; stub[5] = 1;

    using var compressed = new MemoryStream();
    using (var zstd = new ZstdStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(original);

    var compressedBytes = compressed.ToArray();
    var result = new byte[stub.Length + compressedBytes.Length + 16];
    stub.CopyTo(result.AsSpan());
    compressedBytes.CopyTo(result.AsSpan(stub.Length));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(stub.Length + compressedBytes.Length), (ulong)compressedBytes.Length);
    "LALALALA"u8.CopyTo(result.AsSpan(result.Length - 8));
    return result;
  }

  private static byte[] BuildOrigamiWrapper(byte[] original) {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int sectionOffset = optionalOffset + 0xE0;
    const int sectionRaw = 0x400;
    const uint sectionRva = 0x2000;
    const uint cliRva = 0x2000;
    const uint metadataRva = 0x2100;
    const uint methodRva = 0x2500;
    const uint payloadRva = 0x2600;
    const string key = "0123456789ABCDEF0123456789ABCDEF";

    var compressed = DeflateCompressor.Compress(original, DeflateCompressionLevel.Default);
    var encrypted = compressed.ToArray();
    var keyBytes = Encoding.UTF8.GetBytes(key);
    for (var i = 0; i < encrypted.Length; i++)
      encrypted[i] ^= keyBytes[i % keyBytes.Length];

    var image = new byte[0x4000];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), 0xE0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), methodRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 56), 0x4000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 60), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 92), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 96 + 14 * 8), cliRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 96 + 14 * 8 + 4), 0x48);
    ".text\0\0\0"u8.CopyTo(image.AsSpan(sectionOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20), sectionRaw);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0x60000020);

    var cliOffset = sectionRaw + (int)(cliRva - sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset), 0x48);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 8), metadataRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 12), 0x300);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cliOffset + 20), 0x06000001);
    WriteOrigamiMetadata(image, sectionRaw + (int)(metadataRva - sectionRva), key, methodRva);

    var methodOffset = sectionRaw + (int)(methodRva - sectionRva);
    image[methodOffset] = (byte)((14 << 2) | 0x2);
    image[methodOffset + 1] = 0x21;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(methodOffset + 2), payloadRva);
    image[methodOffset + 10] = 0x20;
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(methodOffset + 11), encrypted.Length);
    encrypted.CopyTo(image.AsSpan(sectionRaw + (int)(payloadRva - sectionRva)));
    return image;
  }

  private static void WriteOrigamiMetadata(byte[] image, int offset, string key, uint methodRva) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), 0x424A5342);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 4), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 6), 1);
    var version = "v4.0.30319\0"u8.ToArray();
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset + 12), version.Length);
    version.CopyTo(image.AsSpan(offset + 16));
    var streamHeaderOffset = (offset + 16 + version.Length + 3) & ~3;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(streamHeaderOffset + 2), 2);
    var cursor = streamHeaderOffset + 4;
    WriteStreamHeader(image, ref cursor, 0x100, 0x80, "#~");
    WriteStreamHeader(image, ref cursor, 0x200, 0x80, "#Strings");
    var tables = offset + 0x100;
    image[tables + 4] = 2;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(tables + 8), (1UL << 0) | (1UL << 6));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tables + 24), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tables + 28), 1);
    var method = tables + 42;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(method), methodRva);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 6), 0x16);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 8), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method + 12), 1);
    Encoding.UTF8.GetBytes(key).CopyTo(image.AsSpan(offset + 0x201));
  }

  private static void WriteStreamHeader(byte[] image, ref int cursor, int offset, int size, string name) {
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(cursor), offset);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(cursor + 4), size);
    cursor += 8;
    var nameBytes = Encoding.ASCII.GetBytes(name);
    nameBytes.CopyTo(image.AsSpan(cursor));
    cursor += nameBytes.Length;
    image[cursor++] = 0;
    cursor = (cursor + 3) & ~3;
  }

  private static byte[] BuildSilentPackerElf64(byte[] originalText) {
    const ulong key = 0x1122334455667788;
    const ulong textAddress = 0x401000;
    const int textOffset = 0x100;
    const ulong loaderAddress = 0x402000;
    const int loaderOffset = 0x200;
    const int loaderSize = 0x80;
    const int sectionHeaderOffset = 0x600;

    var image = new byte[0x800];
    image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
    image[4] = 2; image[5] = 1; image[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), loaderAddress);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), sectionHeaderOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), 4);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), 3);

    XorSilentPacker64(originalText, key).CopyTo(image.AsSpan(textOffset));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(loaderOffset + loaderSize - 36), checked((int)((long)textAddress - ((long)loaderAddress + loaderSize - 32))));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 32), key);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 24), textAddress);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 16), (ulong)originalText.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(loaderOffset + loaderSize - 8), loaderAddress);

    var strings = "\0.text\0.dec\0.shstrtab\0"u8.ToArray();
    strings.CopyTo(image.AsSpan(0x500));
    WriteElf64Section(image, sectionHeaderOffset + 64, 1, textAddress, textOffset, originalText.Length);
    WriteElf64Section(image, sectionHeaderOffset + 128, 7, loaderAddress, loaderOffset, loaderSize);
    WriteElf64Section(image, sectionHeaderOffset + 192, 12, 0, 0x500, strings.Length);
    return image;
  }

  private static byte[] XorSilentPacker64(ReadOnlySpan<byte> data, ulong key) {
    var result = data.ToArray();
    var rolling = key;
    for (var i = 0; i < result.Length; i++) {
      result[i] ^= (byte)rolling;
      rolling = (rolling >> 8) | (rolling << 56);
    }
    return result;
  }

  private static void WriteElf64Section(byte[] image, int offset, uint nameIndex, ulong address, int fileOffset, int size) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), nameIndex);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 4), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 8), 0x6);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 16), address);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 24), (ulong)fileOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 32), (ulong)size);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 48), 16);
  }

  private static byte[] MinimalPe() {
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 0x80);
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    return buf;
  }

  private static byte[] BuildHuanWrapper(byte[] original) {
    var key = "0123456789ABCDEF"u8.ToArray();
    var iv = "FEDCBA9876543210"u8.ToArray();
    var encryptedLength = ((original.Length + 15) / 16) * 16;
    var padded = new byte[encryptedLength];
    original.CopyTo(padded.AsSpan());
    var encrypted = AesCryptor.EncryptCbcNoPaddingAny(padded, key, iv);
    var payloadLength = 40 + encrypted.Length;
    var rawSize = (payloadLength + 0x1FF) & ~0x1FF;

    var image = new byte[0x400 + rawSize];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
    "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x8664);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0xF0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98), 0x20B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0xB8), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0xBC), 0x200);

    var section = 0x80 + 24 + 0xF0;
    ".huan\0\0\0"u8.CopyTo(image.AsSpan(section));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), (uint)payloadLength);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), (uint)rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 36), 0x40000040);

    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x400), original.Length);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x404), encrypted.Length);
    key.CopyTo(image.AsSpan(0x408));
    iv.CopyTo(image.AsSpan(0x418));
    encrypted.CopyTo(image.AsSpan(0x428));
    return image;
  }
}
