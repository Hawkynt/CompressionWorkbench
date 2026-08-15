using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Compression.Core.Dictionary.Aplib;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;
using NUnit.Framework;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Round-trip tests for the BeRoEXEPacker static unpacker. Each test packs a
/// known image body the way the packer's own stub expects to find it — call
/// filter, then LZMA or aPLib — and asserts the handler hands back exactly the
/// bytes that went in.
/// </summary>
[TestFixture]
public class BeRoExecutablePackerHandlerTests {
  private const uint ImageBase = 0x400000;
  private const uint PayloadRva = 0x1000;
  private const int EntryOffsetInSection = 0x5F;
  private const int HeaderSize = 0x200;

  [Test, Category("HappyPath")]
  public void LzmaStub_RecoversImageBodyByteIdentically() {
    var body = BuildBody();
    var packed = BuildBeRoPe(body, aplib: false, out var originalEntryPointRva);

    var result = Unpack(packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data, Is.EqualTo(body).AsCollection);
      var metadata = Encoding.UTF8.GetString(result.Artifacts.Single(a => a.Name == "metadata.json").Data);
      Assert.That(metadata, Does.Contain("\"compressionMethod\": \"lzma\""));
      Assert.That(metadata, Does.Contain($"\"originalEntryPointRva\": {originalEntryPointRva}"));
      Assert.That(metadata, Does.Contain("\"originalImportDescriptorRva\": 4660"));
    });
  }

  [Test, Category("HappyPath")]
  public void AplibStub_RecoversImageBodyByteIdentically() {
    var body = BuildBody();
    var packed = BuildBeRoPe(body, aplib: true, out _);

    var result = Unpack(packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decompressed_payload.bin").Data, Is.EqualTo(body).AsCollection);
      Assert.That(Encoding.UTF8.GetString(result.Artifacts.Single(a => a.Name == "metadata.json").Data),
        Does.Contain("\"compressionMethod\": \"aplib\""));
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_RecognizesBeRoPackedPe() {
    var packed = BuildBeRoPe(BuildBody(), aplib: false, out _);

    var match = ExecutablePackerHandlers.DetectBest(packed);

    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("beroexepacker"));
  }

  private static UnpackResult Unpack(byte[] packed) {
    var handler = new BeRoExecutablePackerHandler();
    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);
    return handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());
  }

  /// <summary>
  /// An image body with enough repetition to compress and enough call/jump
  /// opcodes — including a two-byte <c>0F 8x</c> and an <c>E8</c> whose
  /// displacement bytes contain another <c>E8</c> — to exercise the call filter.
  /// </summary>
  private static byte[] BuildBody() {
    var body = new byte[0x2000];
    for (var i = 0; i < body.Length; ++i)
      body[i] = (byte)(i * 7 % 251);
    for (var offset = 0x100; offset < 0x1F00; offset += 0x40) {
      body[offset] = 0xE8;
      BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 1), offset * 3 - 0x800);
      body[offset + 0x10] = 0x0F;
      body[offset + 0x11] = 0x84;
      BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 0x12), -offset);
      body[offset + 0x20] = 0xE9;
      BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 0x21), 0xE8);
    }
    return body;
  }

  /// <summary>
  /// The packer's forward call filter — the exact inverse of the stub's:
  /// scan forward, and for every call/jump/two-byte-jcc turn the relative
  /// displacement into a buffer-absolute one, then skip the four dword bytes.
  /// </summary>
  private static byte[] ApplyForwardCallFilter(byte[] body, long end) {
    var filtered = (byte[])body.Clone();
    for (long i = 0; i <= end && i + 1 < filtered.Length;) {
      long displacement;
      if (filtered[i] is 0xE8 or 0xE9)
        displacement = i + 1;
      else if (filtered[i] == 0x0F && (filtered[i + 1] & 0xF0) == 0x80)
        displacement = i + 2;
      else {
        ++i;
        continue;
      }

      if (displacement + 4 > filtered.Length)
        break;
      var span = filtered.AsSpan((int)displacement, 4);
      BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)(BinaryPrimitives.ReadUInt32LittleEndian(span) + (uint)(displacement + 4)));
      i = displacement + 4;
    }
    return filtered;
  }

  private static byte[] BuildBeRoPe(byte[] body, bool aplib, out uint originalEntryPointRva) {
    var filterEnd = body.Length - 0x40;
    var filtered = ApplyForwardCallFilter(body, filterEnd);

    byte[] payload;
    if (aplib)
      payload = new AplibBuildingBlock().Compress(filtered)[4..];
    else {
      var encoder = new LzmaEncoder();
      using var compressed = new MemoryStream();
      encoder.Encode(compressed, filtered, writeEndMarker: false);
      var stream = compressed.ToArray();
      payload = new byte[13 + stream.Length];
      encoder.Properties.CopyTo(payload, 0);
      BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5), (uint)filtered.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(9), (uint)filtered.Length);
      stream.CopyTo(payload, 13);
    }

    var bodySpan = Align((uint)body.Length, 0x1000);
    var stubRva = PayloadRva + bodySpan;
    var entryRva = stubRva + EntryOffsetInSection;
    var payloadRva = stubRva + (uint)EntryOffsetInSection + 0x80;
    originalEntryPointRva = PayloadRva + 0x40;

    var stub = BuildStub(
      aplib,
      sourceVa: ImageBase + payloadRva,
      destinationVa: ImageBase + PayloadRva,
      compressedSize: (uint)payload.Length,
      filterStartVa: ImageBase + PayloadRva,
      filterEndVa: (uint)(ImageBase + PayloadRva + filterEnd),
      jumpFrom: entryRva,
      jumpTo: ImageBase + originalEntryPointRva - ImageBase);

    var sectionRaw = new byte[EntryOffsetInSection + 0x80 + payload.Length];
    stub.CopyTo(sectionRaw, EntryOffsetInSection);
    payload.CopyTo(sectionRaw, EntryOffsetInSection + 0x80);
    var sectionRawAligned = Align((uint)sectionRaw.Length, 0x200);

    var image = new byte[HeaderSize + sectionRawAligned];
    sectionRaw.CopyTo(image, HeaderSize);

    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    const int peOffset = 0x80;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    image[peOffset] = (byte)'P';
    image[peOffset + 1] = (byte)'E';
    var coff = peOffset + 4;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff), 0x014C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16), 0xE0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 18), 0x010F);
    var opt = coff + 20;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 16), entryRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 28), ImageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 56), stubRva + Align((uint)sectionRaw.Length, 0x1000));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60), HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 92), 16);

    var sectionTable = opt + 0xE0;
    WriteSection(image, sectionTable, "packerBY", virtualAddress: PayloadRva, virtualSize: (uint)body.Length, rawOffset: 0, rawSize: 0, 0xE0000080);
    WriteSection(image, sectionTable + 40, "bero^fr ", virtualAddress: stubRva, virtualSize: (uint)sectionRaw.Length, rawOffset: HeaderSize, rawSize: sectionRawAligned, 0xE0000040);
    return image;
  }

  private static byte[] BuildStub(
      bool aplib, uint sourceVa, uint destinationVa, uint compressedSize,
      uint filterStartVa, uint filterEndVa, uint jumpFrom, uint jumpTo) {
    using var stub = new MemoryStream();
    stub.WriteByte(0x60);
    if (aplib) {
      stub.WriteByte(0xBE);
      WriteUInt32(stub, sourceVa);
      stub.WriteByte(0xBF);
      WriteUInt32(stub, destinationVa);
      stub.Write([0xFC, 0xB2, 0x80, 0x33, 0xDB, 0xA4]);
    } else {
      stub.WriteByte(0x68);
      WriteUInt32(stub, compressedSize);
      stub.WriteByte(0x68);
      WriteUInt32(stub, destinationVa);
      stub.WriteByte(0x68);
      WriteUInt32(stub, sourceVa);
      stub.Write([0xE8, 0x00, 0x00, 0x00, 0x00]);
    }

    // The call unfilter set-up the handler reads its range and bias from.
    stub.WriteByte(0xFC);
    stub.WriteByte(0xBE);
    WriteUInt32(stub, filterStartVa);
    stub.WriteByte(0xB9);
    WriteUInt32(stub, 4);
    stub.Write([0x2B, 0xCE, 0x81, 0xFE]);
    WriteUInt32(stub, filterEndVa);
    stub.Write([0x77, 0x1E]);

    // The import walk the original import-descriptor RVA is read from.
    stub.WriteByte(0xBA);
    WriteUInt32(stub, ImageBase);
    stub.Write([0x8D, 0xB2]);
    WriteUInt32(stub, 0x1234);

    // popad; jmp originalEntryPoint
    stub.WriteByte(0x61);
    stub.WriteByte(0xE9);
    WriteUInt32(stub, (uint)(jumpTo - (jumpFrom + stub.Length + 4)));
    return stub.ToArray();
  }

  private static void WriteUInt32(Stream stream, uint value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void WriteSection(byte[] image, int offset, string name, uint virtualAddress, uint virtualSize, uint rawOffset, uint rawSize, uint characteristics) {
    Encoding.ASCII.GetBytes(name).CopyTo(image.AsSpan(offset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 8), virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 12), virtualAddress);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 16), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 36), characteristics);
  }

  private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;
}
