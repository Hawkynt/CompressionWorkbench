using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;
using FileFormat.Zlib;
using NUnit.Framework;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Round-trip tests for the MoleBox static unpacker: a container is assembled
/// exactly the way the loader expects to read it — LCG-XOR over an LZSS'd
/// loader blob, an IDEA-protected configuration record, and per-section
/// IDEA/zlib bodies — and the handler must hand back the original section
/// bytes.
/// </summary>
[TestFixture]
public class MoleboxExecutablePackerHandlerTests {
  private const uint ImageBase = 0x400000;
  private const int HeaderSize = 0x400;
  private const uint LoaderSectionRva = 0x4000;
  private const uint EntryRva = LoaderSectionRva + 0x100;
  private const uint OriginalEntryRva = 0x1234;
  private const uint KeyBlobRva = EntryRva + 0x100;

  private static readonly byte[] ConfigurationKey =
    [0x0F, 0x1E, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78, 0x87, 0x96, 0xA5, 0xB4, 0xC3, 0xD2, 0xE1, 0xF0];

  private static readonly byte[] SectionKey =
    [0xFD, 0x5F, 0x5D, 0x5A, 0x80, 0xF7, 0x8D, 0xDC, 0x4C, 0xDF, 0x71, 0xCC, 0x75, 0xA5, 0xFC, 0x8F];

  /// <summary>
  /// The published IDEA test vector: key 0001…0008, plaintext 0000 0001 0002
  /// 0003, ciphertext 11FB ED2B 0198 6DE5. Guards the key schedule, the
  /// multiplication modulo 2^16+1 and the round structure in one shot.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Idea_PublishedTestVector_EncryptsAndDecrypts() {
    var key = new byte[16];
    for (var word = 0; word < 8; ++word)
      BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(2 * word), (ushort)(word + 1));
    var plaintext = new byte[8];
    for (var word = 0; word < 4; ++word)
      BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(2 * word), (ushort)word);

    var encryption = MoleboxIdea.ExpandKey(key);
    var ciphertext = MoleboxIdea.ProcessEcb(plaintext, encryption);
    var roundTripped = MoleboxIdea.ProcessEcb(ciphertext, MoleboxIdea.InvertKey(encryption));

    Assert.Multiple(() => {
      Assert.That(Convert.ToHexString(ciphertext), Is.EqualTo("11FBED2B01986DE5"));
      Assert.That(roundTripped, Is.EqualTo(plaintext).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Molebox_RecoversOriginalSectionsByteIdentically() {
    var text = BuildSectionBody(0x800, 0x11);
    var data = BuildSectionBody(0x400, 0x77);
    var packed = BuildMoleboxPe(text, data);

    var handler = new MoleboxExecutablePackerHandler();
    var detection = handler.Detect(packed);
    Assert.That(detection.IsMatch, Is.True);
    var result = handler.Unpack(handler.Parse(packed, detection), new UnpackOptions());

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(Section(result, 0), Is.EqualTo(text).AsCollection);
      Assert.That(Section(result, 1), Is.EqualTo(data).AsCollection);
      Assert.That(result.Artifacts.Any(a => a.Name.StartsWith("sections/002_", StringComparison.Ordinal)), Is.False,
        "the raw-data-less section is dropped by the packer and must not be invented");
      var metadata = Encoding.UTF8.GetString(result.Artifacts.Single(a => a.Name == "metadata.json").Data);
      Assert.That(metadata, Does.Contain($"\"originalEntryPointRva\": {OriginalEntryRva}"));
      Assert.That(metadata, Does.Contain($"\"originalImageBase\": {ImageBase}"));
      Assert.That(result.Artifacts.Any(a => a.Name == "memory_image.bin"), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_RecognizesMoleboxPackedPe() {
    var packed = BuildMoleboxPe(BuildSectionBody(0x800, 0x11), BuildSectionBody(0x400, 0x77));

    var match = ExecutablePackerHandlers.DetectBest(packed);

    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("molebox"));
  }

  private static byte[] Section(UnpackResult result, int index) =>
    result.Artifacts.Single(a => a.Name.StartsWith($"sections/{index:000}_", StringComparison.Ordinal)).Data;

  private static byte[] BuildSectionBody(int length, byte seed) {
    var body = new byte[length];
    for (var i = 0; i < length; ++i)
      body[i] = (byte)(i * seed % 253);
    return body;
  }

  /// <summary>Wraps a section body the way the packer does: zlib, then IDEA over whole blocks.</summary>
  private static byte[] PackSection(byte[] body) {
    var compressed = ZlibStream.Compress(body);
    var framed = new byte[(8 + compressed.Length + 7) / 8 * 8];
    BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(0), (uint)body.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(4), (uint)compressed.Length);
    compressed.CopyTo(framed, 8);
    return MoleboxIdea.ProcessEcb(framed, MoleboxIdea.ExpandKey(SectionKey));
  }

  /// <summary>The keystream the loader XORs its blobs with.</summary>
  private static void ApplyKeystream(Span<byte> data, uint seed, uint additive) {
    var state = seed;
    for (var offset = 0; offset + 4 <= data.Length; offset += 4) {
      state = state * 0x19660D + additive;
      var span = data.Slice(offset, 4);
      BinaryPrimitives.WriteUInt32LittleEndian(span, BinaryPrimitives.ReadUInt32LittleEndian(span) ^ state);
    }
  }

  /// <summary>
  /// Emits an LZSS stream of literals only — every flag bit set. A decoder that
  /// implements the format correctly reproduces the input; one that mis-reads
  /// the flag polarity or the ring set-up does not.
  /// </summary>
  private static byte[] EncodeLzssLiterals(byte[] data) {
    using var stream = new MemoryStream();
    for (var offset = 0; offset < data.Length; offset += 8) {
      var count = Math.Min(8, data.Length - offset);
      stream.WriteByte((byte)((1 << count) - 1));
      stream.Write(data, offset, count);
    }
    return stream.ToArray();
  }

  private static byte[] BuildMoleboxPe(byte[] text, byte[] data) {
    var blobBaseRva = EntryRva + 0x5F;

    // The loader blob: the configuration key in the clear, and the keystreamed
    // key blob whose 0x20 offset holds the section key.
    var blob = new byte[0x400];
    for (var i = 0; i < blob.Length; ++i)
      blob[i] = (byte)(i * 3 % 251);
    ConfigurationKey.CopyTo(blob, (int)(EntryRva + 0x7C - blobBaseRva));
    var keyBlobOffset = (int)(KeyBlobRva - blobBaseRva);
    SectionKey.CopyTo(blob, keyBlobOffset + 0x20);
    ApplyKeystream(blob.AsSpan(keyBlobOffset, 0x80 * 4), ImageBase + KeyBlobRva, 0x3C6EF35F);

    var configuration = new byte[64];
    BinaryPrimitives.WriteUInt32LittleEndian(configuration.AsSpan(0), ImageBase + OriginalEntryRva);
    BinaryPrimitives.WriteUInt32LittleEndian(configuration.AsSpan(4), ImageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(configuration.AsSpan(16), 0b001); // compressed mask
    BinaryPrimitives.WriteUInt32LittleEndian(configuration.AsSpan(20), 0b001); // encrypted mask
    BinaryPrimitives.WriteUInt32LittleEndian(configuration.AsSpan(44), ImageBase + KeyBlobRva);
    // The packer stores the record having run the cipher's decryption over it.
    var configurationRecord = MoleboxIdea.ProcessEcb(configuration, MoleboxIdea.InvertKey(MoleboxIdea.ExpandKey(ConfigurationKey)));

    var lzss = EncodeLzssLiterals(blob);
    var loader = new byte[0x1000];
    configurationRecord.CopyTo(loader, (int)(EntryRva + 0x0B - LoaderSectionRva));
    var headerOffset = (int)(blobBaseRva - LoaderSectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(loader.AsSpan(headerOffset), (uint)blob.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(loader.AsSpan(headerOffset + 4), (uint)lzss.Length);
    lzss.CopyTo(loader, headerOffset + 12);
    // The loader truncates the keystreamed region's start to a 4-byte boundary,
    // so it can reach back into the header's last field.
    var keystreamOffset = (int)((blobBaseRva + 12 & ~3u) - LoaderSectionRva);
    ApplyKeystream(loader.AsSpan(keystreamOffset), ImageBase + blobBaseRva, 0x3C6EF375);

    var sections = new List<(string Name, uint VirtualAddress, uint VirtualSize, byte[] Raw)> {
      ("0", 0x1000, (uint)text.Length, PackSection(text)),
      ("1", 0x2000, (uint)data.Length, data),
      ("2", 0x3000, 0x200, []),
      ("3", LoaderSectionRva, (uint)loader.Length, loader),
      ("4", 0x5000, 0x100, new byte[0x200]),
      ("5", 0x6000, 0x100, new byte[0x200]),
    };

    var rawOffset = (uint)HeaderSize;
    var placed = new List<(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, byte[] Raw)>();
    foreach (var (name, virtualAddress, virtualSize, raw) in sections) {
      var rawSize = (uint)((raw.Length + 0x1FF) / 0x200 * 0x200);
      placed.Add((name, virtualAddress, virtualSize, rawSize == 0 ? 0 : rawOffset, rawSize, raw));
      rawOffset += rawSize;
    }

    var image = new byte[rawOffset];
    foreach (var section in placed)
      if (section.Raw.Length > 0)
        section.Raw.CopyTo(image, (int)section.RawOffset);

    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    const int peOffset = 0x100;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    image[peOffset] = (byte)'P';
    image[peOffset + 1] = (byte)'E';
    var coff = peOffset + 4;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff), 0x014C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2), (ushort)placed.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16), 0xE0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 18), 0x010F);
    var opt = coff + 20;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 16), EntryRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 28), ImageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 56), 0x7000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60), HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 92), 16);

    var sectionTable = opt + 0xE0;
    for (var i = 0; i < placed.Count; ++i) {
      var offset = sectionTable + i * 40;
      var section = placed[i];
      Encoding.ASCII.GetBytes(section.Name).CopyTo(image.AsSpan(offset, 8));
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 8), section.VirtualSize);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 12), section.VirtualAddress);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 16), section.RawSize);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 20), section.RawOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 36), 0xE0000040);
    }
    return image;
  }
}
