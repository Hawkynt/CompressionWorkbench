using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Covers the RLPack container end to end by building executables in the shape the
/// packer produces — a stub whose prologue addresses a block table, one bare
/// compressed stream per original section, and the stub's x86 call/jump filter over
/// the code block — and asserting the handler gives the original sections back.
/// </summary>
[TestFixture]
public class RlPackHandlerTests {
  private const uint CodeRva = 0x1000;
  private const uint DataRva = 0x8000;
  private const int CodeSize = 0x2000;
  private const int DataSize = 0x1000;
  private const byte FilterMarker = 0x5B;

  public enum Core { Lzma, Aplib }

  [Test, Category("HappyPath")]
  public void RlPackHandler_DecompressesEverySectionBlock([Values] Core core) {
    var code = BuildCodeSection();
    var data = BuildDataSection();
    var image = BuildRlPackPe(core, code, data, FilterMarker);

    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "rlpack");
    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);
    var result = handler.Unpack(handler.Parse(image, detection), new());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(Section(result, CodeRva), Is.EqualTo(code).AsCollection, "code section");
      Assert.That(Section(result, DataRva), Is.EqualTo(data).AsCollection, "data section");
      Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True);
      Assert.That(result.Artifacts.Single(a => a.Name.StartsWith($"blocks/block@0x{CodeRva:X}.")).Method,
        Is.EqualTo(core == Core.Lzma ? "lzma" : "aplib"));
    });
  }

  [Test, Category("HappyPath")]
  public void RlPackHandler_PlacesBlocksAtTheirOriginalRvas([Values] Core core) {
    var code = BuildCodeSection();
    var data = BuildDataSection();
    var image = BuildRlPackPe(core, code, data, FilterMarker);

    var result = ExecutablePackerHandlers.TryUnpack(image);
    var region = result!.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data;

    Assert.Multiple(() => {
      Assert.That(region.AsSpan((int)(CodeRva - CodeRva), code.Length).ToArray(), Is.EqualTo(code).AsCollection);
      Assert.That(region.AsSpan((int)(DataRva - CodeRva), data.Length).ToArray(), Is.EqualTo(data).AsCollection);
      // The gap between the two blocks is territory the packer never wrote.
      Assert.That(region.AsSpan(code.Length, (int)(DataRva - CodeRva) - code.Length).ToArray(),
        Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("EdgeCase")]
  public void RlPackHandler_ReversesCallFilterOnlyOnTheCodeBlock() {
    // The data block carries the same E8/marker byte pairs as the code block. The
    // stub filters the code region only, so those must come back untouched.
    var code = BuildCodeSection();
    var data = BuildDataSection();
    var image = BuildRlPackPe(Core.Lzma, code, data, FilterMarker);

    var result = ExecutablePackerHandlers.TryUnpack(image);

    Assert.Multiple(() => {
      Assert.That(Section(result!, DataRva), Is.EqualTo(data).AsCollection);
      Assert.That(data.AsSpan().IndexOf(new byte[] { 0xE8, FilterMarker }), Is.GreaterThanOrEqualTo(0),
        "the fixture must actually contain a call/marker pair in the unfiltered block");
    });
  }

  [Test, Category("EdgeCase")]
  public void RlPackHandler_LeavesCodeFiltered_WhenTheStubDisabledTheFilter() {
    // codeRva/codeSize zeroed is how a packed file says "no call filter"; the block
    // then arrives exactly as compressed, absolute targets and all.
    var code = BuildCodeSection();
    var data = BuildDataSection();
    var image = BuildRlPackPe(Core.Lzma, code, data, FilterMarker, disableFilter: true);

    var result = ExecutablePackerHandlers.TryUnpack(image);

    Assert.Multiple(() => {
      Assert.That(result!.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      // Nothing was filtered on the way in, so nothing may be rewritten on the way out.
      Assert.That(Section(result, CodeRva), Is.EqualTo(code).AsCollection);
      Assert.That(Section(result, DataRva), Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("EdgeCase")]
  public void RlPackHandler_ReportsPayloadLocated_WhenTheStubPrologueIsUnknown() {
    var code = BuildCodeSection();
    var data = BuildDataSection();
    var image = BuildRlPackPe(Core.Lzma, code, data, FilterMarker, corruptStubPrologue: true);

    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "rlpack");
    var detection = handler.Detect(image);
    var result = handler.Unpack(handler.Parse(image, detection), new());

    Assert.Multiple(() => {
      Assert.That(detection.IsMatch, Is.True);
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True);
      Assert.That(result.Artifacts.Any(a => a.Name == "decompressed_payload.bin"), Is.False);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedPackerVersion), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void RlPackHandler_ReportsDecompressionFailed_WhenABlockIsNotACompressedStream() {
    var code = BuildCodeSection();
    var data = BuildDataSection();
    var image = BuildRlPackPe(Core.Lzma, code, data, FilterMarker, corruptFirstBlock: true);

    var handler = ExecutablePackerHandlers.All.Single(h => h.Id == "rlpack");
    var result = handler.Unpack(handler.Parse(image, handler.Detect(image)), new());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.DecompressionFailed), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void RlPackHandler_IsSelectedByTheRegistry([Values] Core core) {
    var image = BuildRlPackPe(core, BuildCodeSection(), BuildDataSection(), FilterMarker);

    var match = ExecutablePackerHandlers.DetectBest(image);

    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("rlpack"));
  }

  private static byte[] Section(UnpackResult result, uint rva) =>
    result.Artifacts.Single(a => a.Name == $"sections/section@0x{rva:X}.bin").Data;

  /// <summary>
  /// A code section with real E8/E9 call sites for the filter to act on. The filler
  /// deliberately stays below 0xE8 so the only call/jump opcodes in the section are the
  /// ones placed here: the filter is ambiguous by construction where an unconverted
  /// E8/E9 happens to be followed by the marker byte, and that ambiguity is the
  /// packer's to avoid, not something this fixture should smuggle in.
  /// </summary>
  private static byte[] BuildCodeSection() {
    var code = new byte[CodeSize];
    for (var i = 0; i < code.Length; ++i)
      code[i] = (byte)(i * 7 % 0xE8);

    // Calls and jumps at known, non-overlapping sites, each targeting somewhere inside
    // the section so the filter's 24-bit absolute form can represent it.
    var sites = new[] { 0x40, 0x120, 0x400, 0x9C0, 0x1200, 0x1AA0 };
    var targets = new[] { 0x1000, 0x30, 0x1FF0, 0x800, 0x60, 0x1234 };
    for (var i = 0; i < sites.Length; ++i) {
      code[sites[i]] = (byte)(i % 2 == 0 ? 0xE8 : 0xE9);
      BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(sites[i] + 1), targets[i] - (sites[i] + 5));
    }

    return code;
  }

  /// <summary>
  /// A non-code section that nonetheless contains E8/E9 bytes followed by the marker,
  /// which the handler must not rewrite.
  /// </summary>
  private static byte[] BuildDataSection() {
    var data = new byte[DataSize];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 97);
    for (var offset = 0x100; offset < 0x400; offset += 0x100) {
      data[offset] = 0xE8;
      data[offset + 1] = FilterMarker;
    }

    return data;
  }

  /// <summary>
  /// The packer side of the stub's filter: every E8/E9 operand becomes the marker byte
  /// followed by the target's 24-bit big-endian offset within the region.
  /// </summary>
  private static byte[] ApplyCallFilter(byte[] code, byte marker) {
    var filtered = (byte[])code.Clone();
    for (var i = 0; i + 5 <= filtered.Length; ) {
      if (filtered[i] != 0xE8 && filtered[i] != 0xE9) {
        ++i;
        continue;
      }

      var target = unchecked((uint)(BinaryPrimitives.ReadInt32LittleEndian(filtered.AsSpan(i + 1)) + i + 5));
      filtered[i + 1] = marker;
      filtered[i + 2] = (byte)(target >> 16);
      filtered[i + 3] = (byte)(target >> 8);
      filtered[i + 4] = (byte)target;
      i += 5;
    }

    return filtered;
  }

  private static byte[] Compress(Core core, byte[] data) {
    if (core == Core.Aplib)
      return AplibBuildingBlock.CompressBare(data);

    // Bare LZMA1 in RLPack's shape: lc=8, lp=0, pb=2, end-marker terminated and with
    // no properties, dictionary size or length field ahead of the range-coded bytes.
    using var output = new MemoryStream();
    new LzmaEncoder(dictionarySize: 1 << 16, lc: 8, lp: 0, pb: 2).Encode(output, data, writeEndMarker: true);
    return output.ToArray();
  }

  private static byte[] BuildRlPackPe(
    Core core,
    byte[] code,
    byte[] data,
    byte marker,
    bool disableFilter = false,
    bool corruptStubPrologue = false,
    bool corruptFirstBlock = false) {
    const int peOffset = 0x80;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionOffset = optionalOffset + optionalSize;
    const int rawOffset = 0x200;
    const uint packedVirtualSize = 0x20000;
    const uint payloadRva = 0x30000;

    var codeBlock = Compress(core, disableFilter ? code : ApplyCallFilter(code, marker));
    var dataBlock = Compress(core, data);

    // Payload section layout: [code block][data block][filter fields][block table][stub].
    using var payload = new MemoryStream();
    var codeBlockOffset = (int)payload.Position;
    payload.Write(codeBlock);
    var dataBlockOffset = (int)payload.Position;
    payload.Write(dataBlock);

    // The three filter fields sit at table-0x1C, table-0x18 and table-0x14, so pad the
    // gap between them and the table start to the 0x1C the stub expects.
    var fieldsOffset = (int)payload.Position;
    Span<byte> fields = stackalloc byte[0x1C];
    BinaryPrimitives.WriteUInt32LittleEndian(fields, disableFilter ? 0 : CodeRva);
    BinaryPrimitives.WriteUInt32LittleEndian(fields[4..], disableFilter ? 0u : (uint)code.Length);
    fields[8] = marker;
    payload.Write(fields);

    var tableOffset = (int)payload.Position;
    Span<byte> entry = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(entry, payloadRva + (uint)codeBlockOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], CodeRva);
    payload.Write(entry);
    BinaryPrimitives.WriteUInt32LittleEndian(entry, payloadRva + (uint)dataBlockOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], DataRva);
    payload.Write(entry);
    payload.Write(new byte[8]); // zero terminator

    // The stub: `pushad; call $+5; mov ebp,[esp]; add esp,4; lea esi,[ebp+imm32]`,
    // where imm32 reaches the block table from entryPoint+6.
    var stubOffset = (int)payload.Position;
    var stub = new byte[] {
      0x60,
      0xE8, 0x00, 0x00, 0x00, 0x00,
      0x8B, 0x2C, 0x24,
      0x83, 0xC4, 0x04,
      0x8D, 0xB5, 0x00, 0x00, 0x00, 0x00,
    };
    BinaryPrimitives.WriteInt32LittleEndian(stub.AsSpan(14), tableOffset - (stubOffset + 6));
    if (corruptStubPrologue)
      stub[0] = 0x90;
    payload.Write(stub);
    Encoding.ASCII.GetBytes("RLPack").CopyTo(payload.GetBuffer().AsSpan(stubOffset + stub.Length));
    payload.Write(new byte[16]);

    var payloadBytes = payload.ToArray();
    if (corruptFirstBlock)
      payloadBytes.AsSpan(codeBlockOffset, Math.Min(64, codeBlock.Length)).Fill(0xFF);

    var image = new byte[rawOffset + payloadBytes.Length];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    Encoding.ASCII.GetBytes("RLPack").CopyTo(image.AsSpan(0x40));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);

    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), payloadRva + (uint)stubOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);

    // The uninitialised destination the stub inflates into.
    ".packed\0"u8.CopyTo(image.AsSpan(sectionOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8), packedVirtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12), CodeRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 36), 0x60000020);

    ".RLPack\0"u8.CopyTo(image.AsSpan(sectionOffset + 40));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 48), (uint)payloadBytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 52), payloadRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 56), (uint)payloadBytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 60), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 76), 0xE0000020);

    payloadBytes.CopyTo(image.AsSpan(rawOffset));
    return image;
  }
}
