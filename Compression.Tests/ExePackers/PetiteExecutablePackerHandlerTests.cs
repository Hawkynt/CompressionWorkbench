using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using Compression.Core.ExecutableUnpacking;
using FileFormat.ExePackers;
using NUnit.Framework;

namespace Compression.Tests.ExePackers;

/// <summary>
/// PEtite's container is a block table behind the entry stub plus DEFLATE
/// streams whose dynamic-Huffman block type is 1 instead of 2. These cases
/// build both by hand so the decoder is pinned without shipping sample
/// binaries.
/// </summary>
[TestFixture]
public class PetiteExecutablePackerHandlerTests {

  [Test, Category("HappyPath")]
  public void Inflate_DecodesStoredBlock() {
    var payload = Enumerable.Range(0, 600).Select(i => (byte)(i * 7)).ToArray();
    var stream = BuildStoredBlock(payload);

    Assert.That(PetiteInflate.TryInflate(stream, 0, payload.Length, out var decoded), Is.True);
    Assert.That(decoded, Is.EqualTo(payload).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Inflate_DecodesDynamicHuffmanBlockAnnouncedAsType1() {
    var payload = BuildCompressiblePayload(16384);
    var stream = BuildPetiteDeflate(payload);

    Assert.That(PetiteInflate.TryInflate(stream, 0, payload.Length, out var decoded), Is.True);
    Assert.That(decoded, Is.EqualTo(payload).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Inflate_RejectsStandardDeflateDynamicBlockType() {
    var payload = BuildCompressiblePayload(16384);
    var stream = RawDeflate(payload);
    Assume.That((stream[0] >> 1) & 3, Is.EqualTo(2), "expected the runtime deflater to emit a dynamic-Huffman block");

    Assert.That(PetiteInflate.TryInflate(stream, 0, payload.Length, out _), Is.False);
  }

  [Test, Category("HappyPath")]
  public void ReverseBranchFilter_RestoresRelativeBranchTargets() {
    var original = new byte[64];
    original[0x10] = 0xE8;
    BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(0x11), -0x15);
    original[0x20] = 0x0F;
    original[0x21] = 0x86;
    BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(0x22), 0x9F);

    var filtered = (byte[])original.Clone();
    BinaryPrimitives.WriteInt32LittleEndian(filtered.AsSpan(0x11), -0x15 + 0x10);
    BinaryPrimitives.WriteInt32LittleEndian(filtered.AsSpan(0x22), 0x9F + 0x20);

    Assert.That(PetiteUnpacker.ReverseBranchFilter(filtered), Is.EqualTo(original).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Handler_ExpandsBlockTableIntoSectionArtifacts() {
    var code = BuildCodePayload(0x2000);
    var data = BuildCompressiblePayload(0x800);
    var image = BuildPetitePe(code, data);
    var handler = new PetiteExecutablePackerHandler();

    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(image, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(result.Artifacts.Single(a => a.Name == "sections/rva_00001000.bin").Data, Is.EqualTo(code).AsCollection);
      Assert.That(result.Artifacts.Single(a => a.Name == "sections/rva_00004000.bin").Data, Is.EqualTo(data).AsCollection);
      Assert.That(result.Artifacts.Any(a => a.Name == "memory_image.bin"), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Handler_ReversesTheBranchTransformOnTheCodeBlock() {
    var code = BuildCodePayload(0x2000);
    var stored = ApplyBranchFilter(code);
    Assert.That(stored, Is.Not.EqualTo(code).AsCollection);
    var image = BuildPetitePe(stored, BuildCompressiblePayload(0x800));
    var handler = new PetiteExecutablePackerHandler();

    var result = handler.Unpack(handler.Parse(image, handler.Detect(image)), new UnpackOptions());

    Assert.That(result.Artifacts.Single(a => a.Name == "sections/rva_00001000.bin").Data, Is.EqualTo(code).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Handler_FallsBackAndReportsFailure_WhenBlockTableIsAbsent() {
    var image = BuildPetitePe(BuildCodePayload(0x2000), BuildCompressiblePayload(0x800), corruptTableReference: true);
    var handler = new PetiteExecutablePackerHandler();

    var result = handler.Unpack(handler.Parse(image, handler.Detect(image)), new UnpackOptions());

    Assert.That(result.Level, Is.LessThan(ExecutableUnpackLevel.PayloadDecompressed));
    Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.DecompressionFailed), Is.True);
  }

  private static byte[] BuildCompressiblePayload(int length) {
    var payload = new byte[length];
    for (var i = 0; i < length; ++i)
      payload[i] = (byte)("petite deflate corpus "[i % 22]);
    return payload;
  }

  /// <summary>Payload whose relative branches all point inside the block, so the filter heuristic recognises it as code.</summary>
  private static byte[] BuildCodePayload(int length) {
    var payload = new byte[length];
    for (var i = 0; i < length; ++i)
      payload[i] = (byte)(0x90 + (i % 3));
    for (var i = 0x40; i + 8 < length; i += 0x40) {
      payload[i] = 0xE8;
      BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(i + 1), 0x20 - 5);
    }
    return payload;
  }

  /// <summary>The packer-side transform: fold each opcode's own block offset into the branch target.</summary>
  private static byte[] ApplyBranchFilter(byte[] block) {
    var copy = (byte[])block.Clone();
    var i = 0;
    while (i < copy.Length - 5) {
      int field;
      int step;
      if (copy[i] is 0xE8 or 0xE9) {
        field = i + 1;
        step = 5;
      } else if (copy[i] == 0x0F && copy[i + 1] is >= 0x80 and <= 0x8F) {
        field = i + 2;
        step = 6;
      } else {
        ++i;
        continue;
      }

      BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(field), BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(field)) + (uint)i);
      i += step;
    }
    return copy;
  }

  private static byte[] BuildStoredBlock(byte[] payload) {
    using var ms = new MemoryStream();
    ms.WriteByte(1); // BFINAL = 1, BTYPE = 0, remaining bits are the byte padding
    ms.WriteByte((byte)payload.Length);
    ms.WriteByte((byte)(payload.Length >> 8));
    ms.WriteByte((byte)~payload.Length);
    ms.WriteByte((byte)(~payload.Length >> 8));
    ms.Write(payload);
    return ms.ToArray();
  }

  private static byte[] RawDeflate(byte[] payload) {
    using var ms = new MemoryStream();
    using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      deflate.Write(payload);
    return ms.ToArray();
  }

  /// <summary>
  /// Turns a standard raw DEFLATE stream into the PEtite dialect by renumbering
  /// the leading block's dynamic-Huffman type from 2 to 1.
  /// </summary>
  private static byte[] BuildPetiteDeflate(byte[] payload) {
    var stream = RawDeflate(payload);
    Assume.That((stream[0] >> 1) & 3, Is.EqualTo(2), "expected the runtime deflater to emit a dynamic-Huffman block");
    stream[0] = (byte)((stream[0] & ~0x06) | 0x02);
    return stream;
  }

  /// <summary>
  /// Emits a PEtite-dialect literal-only dynamic-Huffman block: every literal
  /// gets a 9-bit code and end-of-block a 1-bit code, which is a complete tree
  /// and therefore encoder-independent.
  /// </summary>
  private static byte[] BuildPetiteDynamicBlock(byte[] payload) {
    var order = new[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
    var writer = new BitWriter();
    writer.Write(1, 1);   // BFINAL
    writer.Write(1, 2);   // PEtite: dynamic Huffman tables
    writer.Write(0, 5);   // HLIT  => 257 literal/length codes
    writer.Write(0, 5);   // HDIST => 1 distance code
    writer.Write(14, 4);  // HCLEN => 18 code-length codes
    foreach (var symbol in order.Take(18))
      writer.Write(symbol is 1 or 9 ? 1 : 0, 3);

    // Code-length alphabet: symbols 1 and 9, one bit each => 1 -> "0", 9 -> "1".
    for (var i = 0; i < 256; ++i)
      writer.WriteCode(1, 1);
    writer.WriteCode(0, 1);   // end-of-block symbol gets length 1
    writer.WriteCode(0, 1);   // the single distance code gets length 1

    foreach (var b in payload)
      writer.WriteCode(256 + b, 9);
    writer.WriteCode(0, 1);   // end of block
    return writer.ToArray();
  }

  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _accumulator;
    private int _width;

    public void Write(int value, int width) {
      for (var i = 0; i < width; ++i)
        this.Push((value >> i) & 1);
    }

    public void WriteCode(int code, int width) {
      for (var i = width - 1; i >= 0; --i)
        this.Push((code >> i) & 1);
    }

    public byte[] ToArray() {
      var result = this._bytes.ToList();
      if (this._width > 0)
        result.Add((byte)this._accumulator);
      return [.. result];
    }

    private void Push(int bit) {
      this._accumulator |= bit << this._width;
      if (++this._width != 8)
        return;
      this._bytes.Add((byte)this._accumulator);
      this._accumulator = 0;
      this._width = 0;
    }
  }

  private static byte[] BuildPetitePe(byte[] code, byte[] data, bool corruptTableReference = false) {
    const uint sectionAlignment = 0x1000;
    const uint fileAlignment = 0x200;
    const uint packedRva = 0x1000;
    const uint stubRva = 0x8000;
    const uint tableOffsetInStub = 0x400;
    const int peOffset = 0x80;
    const int optionalSize = 0xE0;
    const int sectionTable = peOffset + 4 + 20 + optionalSize;
    const int headerSize = 0x400;

    var codeStream = BuildPetiteDynamicBlock(code);
    var dataStream = BuildPetiteDynamicBlock(data);
    var packed = new byte[Align((uint)(codeStream.Length + dataStream.Length), fileAlignment)];
    codeStream.CopyTo(packed, 0);
    dataStream.CopyTo(packed, codeStream.Length);

    // Stub: the entry point, the `pop eax; lea ebx, [eax + tableOffset]` the
    // block-table finder keys on, and the table itself.
    var stub = new byte[0x1000];
    stub[0] = 0x58;
    stub[1] = 0x8D;
    stub[2] = 0x98;
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(3), corruptTableReference ? 0x00FFFFFF : tableOffsetInStub);
    var table = (int)tableOffsetInStub;
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(table), packedRva);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(table + 4), (uint)code.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(table + 8), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(table + 16), packedRva + (uint)codeStream.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(table + 20), (uint)data.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(table + 24), 0x4000);

    var image = new byte[headerSize + packed.Length + stub.Length];
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    image[peOffset] = (byte)'P';
    image[peOffset + 1] = (byte)'E';

    var coff = peOffset + 4;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff), 0x014C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 18), 0x0102);

    var optional = coff + 20;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 16), stubRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 20), packedRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 24), 0x4000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 28), 0x400000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 32), sectionAlignment);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 36), fileAlignment);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 56), stubRva + (uint)stub.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 60), headerSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 92), 16);

    WriteSection(image, sectionTable, "", packedRva, stubRva - packedRva, headerSize, (uint)packed.Length, 0xE0000060);
    WriteSection(image, sectionTable + 40, ".petite", stubRva, (uint)stub.Length, (uint)(headerSize + packed.Length), (uint)stub.Length, 0xE2000060);

    packed.CopyTo(image, headerSize);
    stub.CopyTo(image, headerSize + packed.Length);
    return image;
  }

  private static void WriteSection(byte[] image, int offset, string name, uint virtualAddress, uint virtualSize, uint rawOffset, uint rawSize, uint characteristics) {
    for (var i = 0; i < name.Length && i < 8; ++i)
      image[offset + i] = (byte)name[i];
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 8), virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 12), virtualAddress);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 16), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 20), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 36), characteristics);
  }

  private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;
}
