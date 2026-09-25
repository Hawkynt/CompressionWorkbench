using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.AppleSingle;
using FileFormat.Ffu;
using FileFormat.Hdf4;
using FileFormat.Mbox;
using FileFormat.Mz;
using FileFormat.Numpy;
using FileFormat.Pcap;
using FileFormat.Pcapng;
using FileFormat.Jp2;
using FileFormat.Psb;
using FileFormat.Psd;
using FileFormat.Sup;
using FileFormat.UefiFv;
using FileFormat.UImage;
using FileFormat.WebAssembly;

namespace Compression.Tests.Registry;

/// <summary>
/// Verifies archive descriptors whose parsers can consume borrowed memory directly
/// do not fall back through IArchiveFormatOperations' whole-image span copy.
/// </summary>
[TestFixture]
public sealed class NativeSpanArchiveInputTests {
  private const int LargePayloadSize = 4 * 1024 * 1024;
  private const long AllocationLimit = 1024 * 1024;

  private sealed record SpanCase(
    string Name,
    IArchiveFormatOperations Operations,
    byte[] Image,
    string PayloadEntry,
    byte[] ExpectedPayload);

  [Test]
  [Category("Spec")]
  public void ListSpan_MatchesNativeStreamListing() {
    foreach (var testCase in BuildCases(257)) {
      using var stream = new MemoryStream(testCase.Image, writable: false);
      var expected = testCase.Operations.List(stream, null);

      var actual = testCase.Operations.ListSpan(testCase.Image, null);

      Assert.That(actual, Is.EqualTo(expected), testCase.Name);
    }
  }

  [Test]
  [Category("Spec")]
  public void ExtractSpan_WritesExpectedPayloadWithoutCompatibilityBuffer() {
    foreach (var testCase in BuildCases(257)) {
      var directory = Path.Combine(Path.GetTempPath(), $"cwb-span-{Guid.NewGuid():N}");
      try {
        testCase.Operations.ExtractSpan(
          testCase.Image, directory, null, [testCase.PayloadEntry]);

        var path = Path.Combine(directory, testCase.PayloadEntry.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, testCase.Name);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(testCase.ExpectedPayload).AsCollection, testCase.Name);
      } finally {
        if (Directory.Exists(directory))
          Directory.Delete(directory, recursive: true);
      }
    }
  }

  [Test]
  [Category("Spec")]
  public void ListSpan_LargePayloads_DoNotCopyWholeArchive() {
    foreach (var testCase in BuildCases(LargePayloadSize)) {
      // Warm JIT/type initialization outside the measured interval.
      _ = testCase.Operations.ListSpan(testCase.Image, null);

      var before = GC.GetAllocatedBytesForCurrentThread();
      _ = testCase.Operations.ListSpan(testCase.Image, null);
      var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

      Assert.That(allocated, Is.LessThan(AllocationLimit),
        $"{testCase.Name} allocated {allocated:N0} bytes while listing a {testCase.Image.Length:N0}-byte span; " +
        "a native span path must not recreate the universal whole-image compatibility copy.");
    }
  }

  [Test]
  [Category("Spec")]
  public void SourceRangeFormats_ExtractSpan_LargePayloads_DoNotCopyWholeArchive() {
    foreach (var testCase in new[] {
      BuildPsdCase(LargePayloadSize),
      BuildPsbCase(LargePayloadSize),
      BuildJp2Case(LargePayloadSize),
    }) {
      var directory = Path.Combine(Path.GetTempPath(), $"cwb-span-range-{Guid.NewGuid():N}");
      try {
        var warmDirectory = Path.Combine(directory, "warm");
        var warmCase = testCase.Name switch {
          "PSD" => BuildPsdCase(257),
          "PSB" => BuildPsbCase(257),
          _ => BuildJp2Case(257),
        };
        warmCase.Operations.ExtractSpan(
          warmCase.Image, warmDirectory, null, [warmCase.PayloadEntry]);
        Directory.Delete(warmDirectory, recursive: true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        testCase.Operations.ExtractSpan(
          testCase.Image, directory, null, [testCase.PayloadEntry]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.LessThan(AllocationLimit),
          $"{testCase.Name} span extraction allocated {allocated:N0} bytes for a " +
          $"{testCase.Image.Length:N0}-byte image.");
        Assert.That(File.ReadAllBytes(Path.Combine(directory, testCase.PayloadEntry)),
          Is.EqualTo(testCase.ExpectedPayload).AsCollection, testCase.Name);
      } finally {
        if (Directory.Exists(directory))
          Directory.Delete(directory, recursive: true);
      }
    }
  }

  [Test]
  [Category("Spec")]
  public void Ffu_ExtractSpan_FullImage_DoesNotCopyWholeArchive() {
    var testCase = BuildFfuCase(LargePayloadSize);
    var directory = Path.Combine(Path.GetTempPath(), $"cwb-span-ffu-{Guid.NewGuid():N}");

    try {
      // Warm the explicit-interface dispatch and file-system helper.
      var warmDirectory = Path.Combine(directory, "warm");
      testCase.Operations.ExtractSpan(testCase.Image.AsSpan(0, 4096), warmDirectory, null, ["FULL.ffu"]);
      Directory.Delete(warmDirectory, recursive: true);

      var before = GC.GetAllocatedBytesForCurrentThread();
      testCase.Operations.ExtractSpan(testCase.Image, directory, null, ["FULL.ffu"]);
      var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

      Assert.That(File.ReadAllBytes(Path.Combine(directory, "FULL.ffu")),
        Is.EqualTo(testCase.Image).AsCollection);
      Assert.That(allocated, Is.LessThan(AllocationLimit),
        $"FFU span extraction allocated {allocated:N0} bytes for a {testCase.Image.Length:N0}-byte image.");
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  [Category("Spec")]
  public void Sup_ExtractSpan_Epoch_DoesNotCopyWholeArchive() {
    var testCase = BuildSupCase(LargePayloadSize);
    var directory = Path.Combine(Path.GetTempPath(), $"cwb-span-sup-{Guid.NewGuid():N}");

    try {
      var warmCase = BuildSupCase(257);
      var warmDirectory = Path.Combine(directory, "warm");
      warmCase.Operations.ExtractSpan(
        warmCase.Image, warmDirectory, null, [warmCase.PayloadEntry]);
      Directory.Delete(warmDirectory, recursive: true);

      var before = GC.GetAllocatedBytesForCurrentThread();
      testCase.Operations.ExtractSpan(
        testCase.Image, directory, null, [testCase.PayloadEntry]);
      var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

      Assert.That(File.ReadAllBytes(Path.Combine(directory, testCase.PayloadEntry)),
        Is.EqualTo(testCase.ExpectedPayload).AsCollection);
      Assert.That(allocated, Is.LessThan(AllocationLimit),
        $"SUP span extraction allocated {allocated:N0} bytes for a {testCase.Image.Length:N0}-byte image.");
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  private static IReadOnlyList<SpanCase> BuildCases(int payloadSize) => [
    BuildFfuCase(payloadSize),
    BuildHdf4Case(payloadSize),
    BuildNpyCase(payloadSize),
    BuildSupCase(payloadSize),
    BuildWasmCase(payloadSize),
    BuildAppleSingleCase(payloadSize),
    BuildPcapCase(payloadSize),
    BuildPcapngCase(payloadSize),
    BuildPsdCase(payloadSize),
    BuildPsbCase(payloadSize),
    BuildJp2Case(payloadSize),
    BuildMzCase(payloadSize),
    BuildUImageCase(payloadSize),
    BuildUefiFvCase(payloadSize),
    BuildMboxCase(payloadSize),
  ];

  private static SpanCase BuildFfuCase(int payloadSize) {
    const int chunkBytes = 1024;
    using var output = new MemoryStream();
    output.Write("SignedImage\0"u8);
    WriteUInt32LittleEndian(output, 1);
    WriteUInt32LittleEndian(output, 0);
    WriteUInt32LittleEndian(output, 8);
    WriteUInt32LittleEndian(output, 8);
    output.Write(new byte[16]);
    Pad(output, chunkBytes);

    output.Write("ImageFlash  "u8);
    var manifest = "[Manifest]\nDevice=SpanTest\n"u8.ToArray();
    WriteUInt32LittleEndian(output, (uint)manifest.Length);
    WriteUInt32LittleEndian(output, chunkBytes);
    output.Write(manifest);
    Pad(output, chunkBytes);

    var payload = Pattern(payloadSize, 0x31);
    output.Write(payload);
    return new SpanCase(
      "FFU", new FfuFormatDescriptor(), output.ToArray(), "payload.bin", payload);
  }

  private static SpanCase BuildHdf4Case(int payloadSize) {
    var payload = Pattern(payloadSize, 0x42);
    const int payloadOffset = 4 + 6 + 12;
    var image = new byte[payloadOffset + payload.Length];
    Hdf4Reader.Magic.CopyTo(image);

    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(4, 2), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(6, 4), 0);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(10, 2), 702);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(12, 2), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(14, 4), payloadOffset);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(18, 4), (uint)payload.Length);
    payload.CopyTo(image.AsSpan(payloadOffset));

    return new SpanCase(
      "HDF4", new Hdf4FormatDescriptor(), image, "tag_0702_ref_0001.bin", payload);
  }

  private static SpanCase BuildNpyCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0x53);
    const string dictionary = "{'descr': '|u1', 'fortran_order': False, 'shape': (1,), }";
    const int preambleLength = 10;
    var paddedLength = ((preambleLength + dictionary.Length + 1 + 63) / 64) * 64;
    var headerText = dictionary + new string(' ', paddedLength - preambleLength - dictionary.Length - 1) + "\n";
    var header = Encoding.ASCII.GetBytes(headerText);

    var image = new byte[preambleLength + header.Length + payload.Length];
    NpyReader.Magic.CopyTo(image);
    image[6] = 1;
    image[7] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8, 2), (ushort)header.Length);
    header.CopyTo(image.AsSpan(preambleLength));
    payload.CopyTo(image.AsSpan(preambleLength + header.Length));

    return new SpanCase(
      "NPY", new NpyFormatDescriptor(), image, "array.bin", payload);
  }

  private static SpanCase BuildSupCase(int payloadSize) {
    using var output = new MemoryStream();
    WriteSupSegment(output, SupReader.SegPresentationComposition, 90_000, [0xAA, 0xBB]);

    var remaining = payloadSize;
    var chunkIndex = 0;
    while (remaining > 0) {
      var length = Math.Min(ushort.MaxValue, remaining);
      var body = Pattern(length, (byte)(0xE1 + chunkIndex++));
      WriteSupSegment(output, SupReader.SegObjectDefinition, 90_000, body);
      remaining -= length;
    }

    WriteSupSegment(output, SupReader.SegEnd, 91_000, []);
    var image = output.ToArray();
    return new SpanCase(
      "SUP", new SupFormatDescriptor(), image, "subtitle_000.bin", image);
  }

  private static SpanCase BuildWasmCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0x64);
    using var output = new MemoryStream();
    output.Write([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00]);
    output.WriteByte(11); // data section; body is intentionally opaque to this pseudo-archive.
    WriteLeb128(output, (ulong)payload.Length);
    output.Write(payload);

    return new SpanCase(
      "WebAssembly", new WasmFormatDescriptor(), output.ToArray(), "section_11_data.bin", payload);
  }

  private static SpanCase BuildAppleSingleCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0x75);
    const int dataOffset = 26 + 12;
    var image = new byte[dataOffset + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), AppleSingleReader.MagicSingle);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), 0x00020000);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(24, 2), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(26, 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(30, 4), dataOffset);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(34, 4), (uint)payload.Length);
    payload.CopyTo(image.AsSpan(dataOffset));

    return new SpanCase(
      "AppleSingle", new AppleSingleFormatDescriptor(), image, "data_fork.bin", payload);
  }

  private static SpanCase BuildPcapCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0x86);
    var image = new byte[24 + 16 + payload.Length];
    image[0] = 0xA1;
    image[1] = 0xB2;
    image[2] = 0xC3;
    image[3] = 0xD4;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4, 2), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6, 2), 4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16, 4), (uint)Math.Max(payloadSize, 65535));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20, 4), 1);

    const int recordOffset = 24;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(recordOffset, 4), 1_700_000_000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(recordOffset + 4, 4), 123);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(recordOffset + 8, 4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(recordOffset + 12, 4), (uint)payload.Length);
    payload.CopyTo(image.AsSpan(recordOffset + 16));

    return new SpanCase(
      "PCAP", new PcapFormatDescriptor(), image, "packet_0000.bin", payload);
  }

  private static SpanCase BuildPcapngCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0x97);
    var padding = (4 - (payload.Length & 3)) & 3;
    var epbLength = 32 + payload.Length + padding;

    using var output = new MemoryStream();
    Span<byte> shb = stackalloc byte[28];
    BinaryPrimitives.WriteUInt32LittleEndian(shb[0..4], PcapngReader.BtSectionHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(shb[4..8], 28);
    BinaryPrimitives.WriteUInt32LittleEndian(shb[8..12], PcapngReader.ByteOrderMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(shb[12..14], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(shb[14..16], 0);
    BinaryPrimitives.WriteInt64LittleEndian(shb[16..24], -1);
    BinaryPrimitives.WriteUInt32LittleEndian(shb[24..28], 28);
    output.Write(shb);

    Span<byte> idb = stackalloc byte[20];
    idb.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(idb[0..4], PcapngReader.BtInterfaceDescription);
    BinaryPrimitives.WriteUInt32LittleEndian(idb[4..8], 20);
    BinaryPrimitives.WriteUInt16LittleEndian(idb[8..10], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(idb[12..16], (uint)Math.Max(payloadSize, 65535));
    BinaryPrimitives.WriteUInt32LittleEndian(idb[16..20], 20);
    output.Write(idb);

    var epb = new byte[epbLength];
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(0, 4), PcapngReader.BtEnhancedPacket);
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(4, 4), (uint)epbLength);
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(8, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(12, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(16, 4), 1_700_000_000);
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(20, 4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(24, 4), (uint)payload.Length);
    payload.CopyTo(epb.AsSpan(28));
    BinaryPrimitives.WriteUInt32LittleEndian(epb.AsSpan(epbLength - 4, 4), (uint)epbLength);
    output.Write(epb);

    return new SpanCase(
      "PCAPNG", new PcapngFormatDescriptor(), output.ToArray(), "packet_0000.bin", payload);
  }

  private static SpanCase BuildPsdCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0xA1);
    var paddedPayloadLength = payload.Length + (payload.Length & 1);
    var resourceLength = 12 + paddedPayloadLength;
    var image = new byte[26 + 4 + 4 + resourceLength];

    "8BPS"u8.CopyTo(image);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(4, 2), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(12, 2), 3);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(14, 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(18, 4), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(22, 2), 8);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(24, 2), 3);

    var pos = 26;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos, 4), 0);
    pos += 4;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos, 4), (uint)resourceLength);
    pos += 4;

    "8BIM"u8.CopyTo(image.AsSpan(pos, 4));
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(pos + 4, 2), 0x0404);
    image[pos + 6] = 0;
    image[pos + 7] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos + 8, 4), (uint)payload.Length);
    payload.CopyTo(image.AsSpan(pos + 12));

    return new SpanCase(
      "PSD", new PsdFormatDescriptor(), image, "resources/0404_unnamed.bin", payload);
  }

  private static SpanCase BuildPsbCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0xA2);
    var image = new byte[26 + 4 + 4 + 8 + payload.Length];

    "8BPS"u8.CopyTo(image);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(4, 2), 2);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(12, 2), 3);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(14, 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(18, 4), 1);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(22, 2), 8);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(24, 2), 3);

    var pos = 26;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos, 4), 0);
    pos += 4;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos, 4), 0);
    pos += 4;
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(pos, 8), 0);
    pos += 8;
    payload.CopyTo(image.AsSpan(pos));

    return new SpanCase(
      "PSB", new PsbFormatDescriptor(), image, "image_data.bin", payload);
  }

  private static SpanCase BuildJp2Case(int payloadSize) {
    var payload = Pattern(payloadSize, 0xA3);
    var image = new byte[12 + 8 + payload.Length];

    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 12);
    "jP  "u8.CopyTo(image.AsSpan(4, 4));
    image[8] = 0x0D;
    image[9] = 0x0A;
    image[10] = 0x87;
    image[11] = 0x0A;

    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(12, 4), checked((uint)(8 + payload.Length)));
    "jp2c"u8.CopyTo(image.AsSpan(16, 4));
    payload.CopyTo(image.AsSpan(20));

    return new SpanCase(
      "JP2", new Jp2FormatDescriptor(), image, "codestream.j2c", payload);
  }

  private static SpanCase BuildMzCase(int payloadSize) {
    const int headerLength = 32;
    var imageLength = headerLength + payloadSize;
    var blocks = checked((ushort)((imageLength + 511) / 512));
    var bytesInLast = checked((ushort)(imageLength % 512));
    var payload = Pattern(payloadSize, 0xA8);
    var image = new byte[imageLength];

    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(2, 2), bytesInLast);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4, 2), blocks);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8, 2), headerLength / 16);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x18, 2), 0x001C);
    payload.CopyTo(image.AsSpan(headerLength));

    return new SpanCase(
      "MZ", new MzFormatDescriptor(), image, "body.bin", payload);
  }

  private static SpanCase BuildUImageCase(int payloadSize) {
    var payload = Pattern(payloadSize, 0xB9);
    var header = new byte[UImageReader.HeaderSize];
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), UImageReader.Magic);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), 0x12345678);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), 0x80008000);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 0x80008000);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), Crc32(payload));
    header[28] = 5;
    header[29] = 2;
    header[30] = 2;
    header[31] = 0;
    Encoding.ASCII.GetBytes("span-test").CopyTo(header.AsSpan(32));
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), Crc32(header));

    var image = new byte[header.Length + payload.Length];
    header.CopyTo(image, 0);
    payload.CopyTo(image, header.Length);
    return new SpanCase(
      "U-Boot uImage", new UImageFormatDescriptor(), image, UImageWriter.PayloadName, payload);
  }

  private static SpanCase BuildUefiFvCase(int payloadSize) {
    const int headerLength = 72;
    const int ffsHeaderLength = 24;
    var payload = Pattern(payloadSize, 0xCA);
    var fileSize = checked(ffsHeaderLength + payload.Length);
    var fvLength = (headerLength + fileSize + 7) & ~7;
    var image = new byte[fvLength];
    var fileGuid = Guid.Parse("11223344-5566-7788-99AA-BBCCDDEEFF00");
    var fsGuid = Guid.Parse("8C8CE578-8A3D-4F1C-9935-896185C32DD3");

    fsGuid.TryWriteBytes(image.AsSpan(16, 16));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32, 8), (ulong)fvLength);
    "_FVH"u8.CopyTo(image.AsSpan(40, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44, 4), 0x0004FEFF);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(48, 2), headerLength);
    image[55] = 2;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(56, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(60, 4), (uint)fvLength);

    fileGuid.TryWriteBytes(image.AsSpan(headerLength, 16));
    image[headerLength + 18] = 0x07;
    image[headerLength + 19] = 0;
    image[headerLength + 20] = (byte)(fileSize & 0xFF);
    image[headerLength + 21] = (byte)((fileSize >> 8) & 0xFF);
    image[headerLength + 22] = (byte)((fileSize >> 16) & 0xFF);
    image[headerLength + 23] = 0xF8;
    payload.CopyTo(image.AsSpan(headerLength + ffsHeaderLength));

    var entryName = $"{fileGuid:D}_{UefiFvReader.ShortTypeTag(0x07)}.bin";
    return new SpanCase(
      "UEFI FV", new UefiFvFormatDescriptor(), image, entryName, payload);
  }

  private static SpanCase BuildMboxCase(int payloadSize) {
    var body = Pattern(payloadSize, 0xDB);
    var separator = "From sender@example.org Mon Jan  1 00:00:00 2024\n"u8.ToArray();
    var headers = "From: sender@example.org\nSubject: Span Test\nDate: Mon, 01 Jan 2024 00:00:00 +0000\n\n"u8.ToArray();
    var eml = new byte[headers.Length + body.Length + 1];
    headers.CopyTo(eml, 0);
    body.CopyTo(eml, headers.Length);
    eml[^1] = (byte)'\n';

    var image = new byte[separator.Length + eml.Length];
    separator.CopyTo(image, 0);
    eml.CopyTo(image, separator.Length);

    return new SpanCase(
      "mbox", new MboxFormatDescriptor(), image, "message_00_Span_Test.eml", eml);
  }

  private static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var value in data) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return crc ^ 0xFFFFFFFFu;
  }

  private static byte[] Pattern(int length, byte seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(seed + i * 17);
    return result;
  }

  private static void WriteSupSegment(
      Stream output, byte type, uint pts, ReadOnlySpan<byte> body, uint dts = 0) {
    if (body.Length > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(body));

    Span<byte> header = stackalloc byte[13];
    header[0] = (byte)'P';
    header[1] = (byte)'G';
    BinaryPrimitives.WriteUInt32BigEndian(header[2..6], pts);
    BinaryPrimitives.WriteUInt32BigEndian(header[6..10], dts);
    header[10] = type;
    BinaryPrimitives.WriteUInt16BigEndian(header[11..13], (ushort)body.Length);
    output.Write(header);
    output.Write(body);
  }

  private static void WriteUInt32LittleEndian(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void Pad(MemoryStream output, int alignment) {
    var padding = (alignment - (int)(output.Length % alignment)) % alignment;
    if (padding > 0)
      output.Write(new byte[padding]);
  }

  private static void WriteLeb128(Stream output, ulong value) {
    do {
      var valueByte = (byte)(value & 0x7F);
      value >>= 7;
      if (value != 0)
        valueByte |= 0x80;
      output.WriteByte(valueByte);
    } while (value != 0);
  }
}
