using System;
using System.Buffers.Binary;
using System.Linq;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.ExecutableUnpacking;
using FileFormat.ExePackers;
using NUnit.Framework;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Exercises the JDPack container against synthetic images built to the layout the real
/// loader stub implements: a rolling-XOR-obfuscated stub carrying a directory of
/// (destination RVA, compressed size) records, each pointing at a byte-XOR-obfuscated
/// aPLib stream in the simplified no-last-was-match dialect.
/// </summary>
[TestFixture]
public class JdpackExecutablePackerHandlerTests {

  [Test, Category("HappyPath")]
  public void Unpack_RecoversEveryBlobByteExactly() {
    var text = Payload(0x1800, 7);
    var data = Payload(0x600, 11);
    var packed = BuildJdpackPe([(TextRva, text), (DataRva, data)], streamKey: 0x5A, originalEntryPoint: 0x1234);

    var handler = new JdpackExecutablePackerHandler();
    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(Artifact(result, $"decompressed/rva_{TextRva:x8}.bin"), Is.EqualTo(text).AsCollection);
      Assert.That(Artifact(result, $"decompressed/rva_{DataRva:x8}.bin"), Is.EqualTo(data).AsCollection);
      Assert.That(result.Artifacts.Any(a => a.Name == "reconstructed/reconstructed.exe"), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Unpack_RestoresOriginalEntryPointInReconstruction() {
    const uint oep = 0x1234;
    var packed = BuildJdpackPe([(TextRva, Payload(0x400, 3))], streamKey: 0x11, originalEntryPoint: oep);

    var handler = new JdpackExecutablePackerHandler();
    var result = handler.Unpack(handler.Parse(packed, handler.Detect(packed)), new UnpackOptions());

    var rebuilt = Artifact(result, "reconstructed/reconstructed.exe");
    var peOffset = BinaryPrimitives.ReadInt32LittleEndian(rebuilt.AsSpan(0x3C));
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(peOffset + 24 + 16)), Is.EqualTo(oep),
        "the reconstruction must jump to the packed program's original entry point, not the stub");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(rebuilt.AsSpan(peOffset + 6)), Is.EqualTo((ushort)1),
        "the loader stub's own section must be dropped from the reconstruction");
    });
  }

  [Test, Category("EdgeCase")]
  public void Unpack_LocatesPayloadButDecompressesNothing_WhenStubIsAbsent() {
    var packed = BuildJdpackPe([(TextRva, Payload(0x400, 3))], streamKey: 0x11, originalEntryPoint: 0x1234);
    // Blank the stub prologue: the section is still named .jdpack, but nothing can be read from it.
    var stubStart = StubRawOffset(packed);
    packed.AsSpan(stubStart, 16).Clear();

    var handler = new JdpackExecutablePackerHandler();
    var result = handler.Unpack(handler.Parse(packed, handler.Detect(packed)), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Any(a => a.Name == "compressed_payload.bin"), Is.True);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.UnsupportedCompressionMethod), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void Unpack_RejectsBlobs_WhenTheStreamKeyIsWrong() {
    var packed = BuildJdpackPe([(TextRva, Payload(0x800, 5))], streamKey: 0x5A, originalEntryPoint: 0x1234);
    // Flip the stored stream key so every blob de-obfuscates to noise.
    var stubStart = StubRawOffset(packed);
    packed[stubStart + StreamKeyValueOffset] ^= 0xFF;

    var handler = new JdpackExecutablePackerHandler();
    var result = handler.Unpack(handler.Parse(packed, handler.Detect(packed)), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.LessThan(ExecutableUnpackLevel.PayloadDecompressed),
        "a mis-keyed stream must not be reported as decompressed");
      Assert.That(result.Artifacts.Any(a => a.Name.StartsWith("decompressed/", StringComparison.Ordinal)), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void AplibNoLastWasMatchDialect_RoundTrips() {
    var data = Payload(0x2000, 13);
    var compressed = AplibBuildingBlock.CompressBare(data, AplibDialect.NoLastWasMatch);
    var restored = AplibBuildingBlock.DecompressRaw(compressed, data.Length + 16, AplibDialect.NoLastWasMatch, out var endMarker, out var used);

    byte[]? viaStandard = null;
    try {
      viaStandard = AplibBuildingBlock.DecompressRaw(compressed, data.Length + 16, AplibDialect.Standard, out _, out _);
    } catch (InvalidDataException) {
      // Reading the simplified dialect as the documented one is expected to derail.
    }

    Assert.Multiple(() => {
      Assert.That(restored, Is.EqualTo(data).AsCollection);
      Assert.That(endMarker, Is.True);
      Assert.That(used, Is.EqualTo(compressed.Length));
      Assert.That(viaStandard is null || !viaStandard.SequenceEqual(data), Is.True,
        "the payload must actually distinguish the dialects, otherwise this test proves nothing");
    });
  }

  // ── Synthetic JDPack image ──────────────────────────────────────────────────

  private const uint TextRva = 0x1000;
  private const uint DataRva = 0x3000;
  private const uint StubRva = 0x5000;
  private const int PeOffset = 0x80;
  private const int OptionalSize = 0xE0;
  private const int SectionTableOffset = PeOffset + 24 + OptionalSize;
  private const int HeadersSize = 0x200;

  // Offsets inside the synthetic stub section.
  private const int EncryptedStart = 0x40;
  private const int EncryptedLength = 0x180;
  private const int DirectoryRefOffset = 0x40;
  private const int StreamKeyRefOffset = 0x60;
  private const int EntryPointRefOffset = 0x80;
  private const int CountOffset = 0xA0;
  private const int TableOffset = CountOffset + 4;
  private const int StreamKeyValueOffset = 0x140;
  private const int EntryPointValueOffset = 0x150;
  private const int SeedOffset = 0x1E0;
  private const int StubSize = 0x200;

  // ebp is (stub base + 6 - K), so every ebp-relative displacement in the stub is
  // (offset + K - 6). K is arbitrary as long as the displacements stay unsigned.
  private const uint EbpBias = 0x400000;

  private static uint Displacement(int offset) => (uint)offset + EbpBias - 6;

  /// <summary>
  /// Builds a deterministic payload out of fresh bytes interleaved with copies of earlier
  /// runs, so the encoder emits back-to-back matches. Those are exactly the tokens where the
  /// two aPLib dialects disagree — a payload of plain literals would pass under either.
  /// </summary>
  private static byte[] Payload(int length, int seed) {
    var buffer = new byte[length];
    var state = (uint)seed | 1u;
    var pos = 0;
    while (pos < length) {
      state = state * 1103515245 + 12345;
      if (pos > 64 && (state >> 16 & 3) != 0) {
        var from = (int)((state >> 8) % (uint)pos);
        var run = Math.Min(4 + (int)(state >> 4 & 31), length - pos);
        for (var i = 0; i < run; ++i) buffer[pos + i] = buffer[(from + i) % pos];
        pos += run;
      } else
        buffer[pos++] = (byte)(state >> 24);
    }
    return buffer;
  }

  private static byte[] Artifact(UnpackResult result, string name) =>
    result.Artifacts.Single(a => a.Name == name).Data;

  private static int StubRawOffset(byte[] image) {
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(PeOffset + 6));
    for (var i = 0; i < sectionCount; ++i) {
      var entry = SectionTableOffset + i * 40;
      if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry + 12)) == StubRva)
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry + 20));
    }
    throw new InvalidOperationException("stub section missing");
  }

  private static byte[] BuildStub((uint Rva, uint CompressedSize)[] blobs, byte streamKey, uint originalEntryPoint) {
    var stub = new byte[StubSize];

    // pushal; call $+5; pop ebp; mov edx,ebp; sub ebp,K
    ReadOnlySpan<byte> prologue = [0x60, 0xE8, 0x00, 0x00, 0x00, 0x00, 0x5D, 0x8B, 0xD5, 0x81, 0xED];
    prologue.CopyTo(stub);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(prologue.Length), EbpBias);

    // mov ecx,len / lea esi,[ebp+start] / mov al,[ebp+seed] / mov bl,[esi] / xor al,bl /
    // mov [esi],al / mov [ebp+seed],bl / inc esi / loop
    var p = 0x0F;
    stub[p++] = 0xB9; BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), EncryptedLength); p += 4;
    stub[p++] = 0x8D; stub[p++] = 0xB5; BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), Displacement(EncryptedStart)); p += 4;
    stub[p++] = 0x8A; stub[p++] = 0x85; BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), Displacement(SeedOffset)); p += 4;
    stub[p++] = 0x8A; stub[p++] = 0x1E; stub[p++] = 0x32; stub[p++] = 0xC3; stub[p++] = 0x88; stub[p++] = 0x06;
    stub[p++] = 0x88; stub[p++] = 0x9D; BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), Displacement(SeedOffset)); p += 4;
    stub[p++] = 0x46; stub[p++] = 0xE2; stub[p] = 0xEB;

    // mov esi,[ebp+count] / mov eax,ebp / push esi / push eax / mov ecx,[eax+count+8]
    p = DirectoryRefOffset;
    stub[p++] = 0x8B; stub[p++] = 0xB5; BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), Displacement(CountOffset)); p += 4;
    stub[p++] = 0x8B; stub[p++] = 0xC5; stub[p++] = 0x56; stub[p++] = 0x50;
    stub[p++] = 0x8B; stub[p++] = 0x88; BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), Displacement(CountOffset + 8));

    // lodsb / xor al,[ebp+key] / stosb / loop
    p = StreamKeyRefOffset;
    stub[p++] = 0xAC; stub[p++] = 0x32; stub[p++] = 0x85;
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), Displacement(StreamKeyValueOffset)); p += 4;
    stub[p++] = 0xAA; stub[p++] = 0xE2; stub[p] = 0xF6;

    // mov eax,[ebp+oep] / add eax,edx / mov [esp+0x1c],eax / popal / push eax / ret
    p = EntryPointRefOffset;
    stub[p++] = 0x8B; stub[p++] = 0x85; BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(p), Displacement(EntryPointValueOffset)); p += 4;
    stub[p++] = 0x03; stub[p++] = 0xC2; stub[p++] = 0x89; stub[p++] = 0x44; stub[p++] = 0x24; stub[p++] = 0x1C;
    stub[p++] = 0x61; stub[p++] = 0x50; stub[p] = 0xC3;

    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(CountOffset), (uint)blobs.Length);
    for (var i = 0; i < blobs.Length; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(TableOffset + i * 8), blobs[i].Rva);
      BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(TableOffset + i * 8 + 4), blobs[i].CompressedSize);
    }
    stub[StreamKeyValueOffset] = streamKey;
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(EntryPointValueOffset), originalEntryPoint);

    // Apply the rolling XOR the stub undoes at run time: cipher[i] = plain[i] ^ seed,
    // seed' = cipher[i], seeded from a byte kept outside the encrypted range.
    var seed = stub[SeedOffset];
    for (var i = EncryptedStart; i < EncryptedStart + EncryptedLength; ++i) {
      var cipher = (byte)(stub[i] ^ seed);
      stub[i] = cipher;
      seed = cipher;
    }
    return stub;
  }

  private static byte[] BuildJdpackPe((uint Rva, byte[] Content)[] sections, byte streamKey, uint originalEntryPoint) {
    var blobs = sections
      .Select(s => {
        var stream = AplibBuildingBlock.CompressBare(s.Content, AplibDialect.NoLastWasMatch);
        for (var i = 0; i < stream.Length; ++i) stream[i] ^= streamKey;
        return (s.Rva, Stream: stream);
      })
      .ToArray();

    var stub = BuildStub([.. blobs.Select(b => (b.Rva, (uint)b.Stream.Length))], streamKey, originalEntryPoint);
    var names = new[] { ".text", ".data" };

    var rawSizes = blobs.Select(b => Align((uint)b.Stream.Length, 0x200)).Append(Align((uint)stub.Length, 0x200)).ToArray();
    var rawOffsets = new uint[rawSizes.Length];
    var cursor = (uint)HeadersSize;
    for (var i = 0; i < rawSizes.Length; ++i) { rawOffsets[i] = cursor; cursor += rawSizes[i]; }

    var image = new byte[cursor];
    image[0] = (byte)'M'; image[1] = (byte)'Z';
    "JDPack"u8.CopyTo(image.AsSpan(0x40));
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), PeOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(PeOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 6), (ushort)(blobs.Length + 1));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 20), OptionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(PeOffset + 22), 0x010F);

    var optional = PeOffset + 24;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 16), StubRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 28), 0x00400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 56), StubRva + 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 60), HeadersSize);

    for (var i = 0; i < blobs.Length; ++i)
      WriteSection(image, i, names[i], blobs[i].Rva, (uint)sections[i].Content.Length, rawOffsets[i], rawSizes[i], blobs[i].Stream);
    WriteSection(image, blobs.Length, ".jdpack", StubRva, (uint)stub.Length, rawOffsets[^1], rawSizes[^1], stub);
    return image;
  }

  private static void WriteSection(byte[] image, int index, string name, uint rva, uint virtualSize, uint rawOffset, uint rawSize, byte[] content) {
    var entry = SectionTableOffset + index * 40;
    System.Text.Encoding.ASCII.GetBytes(name).CopyTo(image.AsSpan(entry, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 8), virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 12), rva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 16), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 36), 0xE0000020);
    content.CopyTo(image.AsSpan((int)rawOffset));
  }

  private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;
}
