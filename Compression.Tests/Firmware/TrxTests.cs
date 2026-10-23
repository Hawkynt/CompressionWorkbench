#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.FirmwareCommon;
using FileFormat.Trx;

namespace Compression.Tests.Firmware;

[TestFixture]
public sealed class TrxTests {

  private static readonly TrxFormatDescriptor Descriptor = new();

  private static byte[] Bytes(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static byte[] Create(params (string Name, byte[] Data)[] inputs) {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [.. inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> Extract(byte[] image) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_trx_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(image)) Descriptor.Extract(ms, dir, null, null);
      return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  /// <summary>A TRX built by hand from the binwalk structure, independent of the writer.</summary>
  private static byte[] HandBuilt(ushort version, params byte[][] parts) {
    var header = version >= 2 ? 32 : 28;
    var length = header + parts.Sum(p => p.Length);
    var image = new byte[length];
    BinaryPrimitives.WriteUInt32LittleEndian(image, 0x30524448);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)length);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(14), version);
    var offset = header;
    for (var i = 0; i < parts.Length; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16 + 4 * i), (uint)offset);
      parts[i].CopyTo(image, offset);
      offset += parts[i].Length;
    }
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), FirmwareCrc32.Update(0xFFFFFFFF, image.AsSpan(12)));
    return image;
  }

  [Test, Category("HappyPath")]
  public void GivenAHandBuiltV1Image_WhenExtracted_ThenEachPartitionComesBackAndTheCrcChecks() {
    var image = HandBuilt(1, Bytes(100, 1), Bytes(200, 2), Bytes(48, 3));
    var files = Extract(image);
    Assert.Multiple(() => {
      Assert.That(files["partition_0.bin"], Is.EqualTo(Bytes(100, 1)));
      Assert.That(files["partition_1.bin"], Is.EqualTo(Bytes(200, 2)));
      Assert.That(files["partition_2.bin"], Is.EqualTo(Bytes(48, 3)));
      Assert.That(Encoding.UTF8.GetString(files["metadata.ini"]), Does.Contain("crc_ok = true"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAV2ImageWithFourPartitions_WhenListed_ThenAllFourAppear() {
    var image = HandBuilt(2, Bytes(4, 1), Bytes(8, 2), Bytes(12, 3), Bytes(16, 4));
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Count(e => e.Name.StartsWith("partition_")), Is.EqualTo(4));
  }

  [Test, Category("RoundTrip")]
  public void GivenPartitions_WhenCreatedAndExtractedAndRecreated_ThenTheBytesAreIdentical() {
    var image = Create(("partition_0.bin", Bytes(1001, 1)), ("partition_1.bin", Bytes(5000, 2)), ("partition_2.bin", Bytes(77, 3)),
      ("metadata.ini", Encoding.ASCII.GetBytes("[trx]\nalignment_2 = 4096\n")));
    var again = Create([.. Extract(image).Select(kv => (kv.Key, kv.Value))]);
    var parsed = TrxImage.Parse(image);
    Assert.Multiple(() => {
      Assert.That(again, Is.EqualTo(image));
      Assert.That(parsed.CrcValid, Is.True);
      Assert.That(parsed.Offsets[2] % 4096, Is.Zero, "partition 2 honours its alignment");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenTheKernelIsReplacedByALargerOne_ThenTheRootfsKeepsItsAlignmentAndTheCrcIsRecomputed() {
    var image = Create(("partition_0.bin", Bytes(100, 1)), ("partition_1.bin", Bytes(100, 2)), ("partition_2.bin", Bytes(100, 3)),
      ("metadata.ini", Encoding.ASCII.GetBytes("[trx]\nalignment_2 = 65536\n")));
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("partition_1.bin", Bytes(70000, 4))]);
    var edited = TrxImage.Parse(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(edited.CrcValid, Is.True);
      // A partition runs to the next offset, so the alignment gap and the final 4 KiB fill read as part of it.
      Assert.That(edited.Partitions[1].AsSpan(0, 70000).ToArray(), Is.EqualTo(Bytes(70000, 4)));
      Assert.That(edited.Partitions[1].AsSpan(70000).ContainsAnyExcept((byte)0), Is.False);
      Assert.That(edited.Partitions[2].AsSpan(0, 100).ToArray(), Is.EqualTo(Bytes(100, 3)));
      Assert.That(edited.Offsets[2], Is.EqualTo(0x20000u));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnImage_WhenAPartitionIsRemoved_ThenTheLaterOneMovesUp() {
    var image = Create(("partition_0.bin", Bytes(10, 1)), ("partition_1.bin", Bytes(20, 2)), ("partition_2.bin", Bytes(30, 3)));
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Remove(ms, ["partition_1.bin"]);
    var edited = TrxImage.Parse(ms.ToArray());
    Assert.Multiple(() => {
      Assert.That(edited.Partitions, Has.Count.EqualTo(2));
      Assert.That(edited.Partitions[0].AsSpan(0, 10).ToArray(), Is.EqualTo(Bytes(10, 1)));
      Assert.That(edited.Partitions[1].AsSpan(0, 30).ToArray(), Is.EqualTo(Bytes(30, 3)));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenThreePartitions_WhenCreated_ThenTheLayoutIsOpenWrtTrxs() {
    // Measured from OpenWrt firmware-utils `trx -o out -f p0 -f p1 -f p2` with 1001/5000/77-byte inputs:
    // partitions at 0x1C, 0x408, 0x1790 (each input padded to 4 bytes) and the length rounded to 4 KiB.
    var image = Create(("partition_0.bin", Bytes(1001, 1)), ("partition_1.bin", Bytes(5000, 2)), ("partition_2.bin", Bytes(77, 3)));
    var parsed = TrxImage.Parse(image);
    Assert.Multiple(() => {
      Assert.That(parsed.Offsets, Is.EqualTo(new uint[] { 0x1C, 0x408, 0x1790 }));
      Assert.That(image.Length, Is.EqualTo(0x2000));
      Assert.That(parsed.Length, Is.EqualTo(0x2000u));
    });
  }

  [Test, Category("Exception")]
  public void GivenAWrongCrc_WhenListed_ThenItIsReportedAndEditsAreRefused() {
    var image = HandBuilt(1, Bytes(100, 1));
    image[^1] ^= 0xFF;
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Multiple(() => {
      Assert.That(Encoding.UTF8.GetString(Extract(image)["metadata.ini"]), Does.Contain("crc_ok = false"));
      Assert.That(() => Descriptor.Add(ms, [ArchiveInputInfo.InMemory("partition_0.bin", [1])]), Throws.TypeOf<NotSupportedException>());
      Assert.That(ms.ToArray(), Is.EqualTo(image));
    });
  }

  [Test, Category("Exception")]
  public void GivenALengthPastTheEnd_WhenListed_ThenItFallsBackWithoutThrowing() {
    var image = HandBuilt(1, Bytes(100, 1));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 10_000);
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "FULL.trx" }));
  }

  [TestCase((ushort)0), TestCase((ushort)3)]
  [Category("Exception")]
  public void GivenAnUnknownVersion_WhenParsed_ThenItIsRejected(ushort version) {
    var image = HandBuilt(1, Bytes(10, 1));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(14), version);
    Assert.That(() => TrxImage.Parse(image), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("Boundary")]
  public void GivenFourPartitionsForV1_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(("partition_0.bin", [1]), ("partition_1.bin", [1]), ("partition_2.bin", [1]), ("partition_3.bin", [1])), Throws.ArgumentException);

  [Test, Category("Boundary")]
  public void GivenNoPartitions_WhenCreatedOrPurged_ThenThereIsNoEmptyTrx() {
    var image = HandBuilt(1, Bytes(10, 1));
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Multiple(() => {
      Assert.That(() => Create(), Throws.ArgumentException);
      Assert.That(() => Descriptor.Purge(ms), Throws.TypeOf<NotSupportedException>());
    });
  }

  [Test, Category("Exception")]
  public void GivenAGapInPartitionNumbers_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create(("partition_0.bin", [1]), ("partition_2.bin", [1])), Throws.ArgumentException);

  // ── OpenWrt firmware-utils vectors (see ReferenceVectors/README.md) ──

  /// <summary>Byte i of input k, as the vector generator wrote it: (i * (31 + 2k) + 7k + 1) &amp; 0xFF.</summary>
  private static byte[] VectorInput(int k, int length) => Enumerable.Range(0, length).Select(i => (byte)((i * (31 + 2 * k) + 7 * k + 1) & 0xFF)).ToArray();

  [TestCase("openwrt-trx-v1.trx", "", TestName = "GivenTheInputsOpenWrtsTrxPacked_WhenWeCreateTheImage_ThenTheBytesAreIdentical")]
  [TestCase("openwrt-otrx-aligned.trx", "[trx]\nalignment_2 = 65536\n", TestName = "GivenTheInputsOtrxPackedWithA64KAlignment_WhenWeCreateTheImage_ThenTheBytesAreIdentical")]
  public void OpenWrtVector_IsReproducedByteForByte(string vector, string ini) {
    var expected = FirmwareVectors.Load(vector);
    var inputs = new List<(string, byte[])> {
      ("partition_0.bin", VectorInput(0, 1001)), ("partition_1.bin", VectorInput(1, 5000)), ("partition_2.bin", VectorInput(2, 77)),
    };
    if (ini.Length > 0) inputs.Add(("metadata.ini", Encoding.ASCII.GetBytes(ini)));
    Assert.That(Create([.. inputs]), Is.EqualTo(expected));
  }

  [TestCase("openwrt-trx-v1.trx")]
  [TestCase("openwrt-otrx-aligned.trx")]
  public void GivenAnOpenWrtVector_WhenExtracted_ThenEachInputComesBackAtTheStartOfItsPartition(string vector) {
    var files = Extract(FirmwareVectors.Load(vector));
    Assert.Multiple(() => {
      Assert.That(files["partition_0.bin"].AsSpan(0, 1001).ToArray(), Is.EqualTo(VectorInput(0, 1001)));
      Assert.That(files["partition_1.bin"].AsSpan(0, 5000).ToArray(), Is.EqualTo(VectorInput(1, 5000)));
      Assert.That(files["partition_2.bin"].AsSpan(0, 77).ToArray(), Is.EqualTo(VectorInput(2, 77)));
      Assert.That(Encoding.UTF8.GetString(files["metadata.ini"]), Does.Contain("crc_ok = true").And.Contain("canonical = true"));
    });
  }

  [Test, Category("ArchiveExternal")]
  public void GivenAnEditedImage_WhenOtrxChecksIt_ThenItIsAValidTrx() {
    var otrx = FirmwareUtilsOracle.Require("otrx");
    var image = Create(("partition_0.bin", Bytes(3000, 1)), ("partition_1.bin", Bytes(9000, 2)), ("partition_2.bin", Bytes(500, 3)));
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("partition_1.bin", Bytes(20000, 4))]);
    var (code, output) = FirmwareUtilsOracle.RunOn(otrx, ms.ToArray(), "check", "{0}");
    Assert.That(code, Is.Zero, output);
    Assert.That(output, Does.Contain("Found a valid TRX version 1"));
  }

  [Test, Category("ArchiveExternal")]
  public void GivenAnImageWeWrote_WhenBinwalkScansIt_ThenItRecognisesTheTrxWhichRequiresAValidCrc() {
    var image = Create(("partition_0.bin", Bytes(3000, 1)), ("partition_1.bin", Bytes(9000, 2)), ("partition_2.bin", Bytes(500, 3)));
    // binwalk wraps its description column, so compare with the whitespace collapsed.
    var scan = System.Text.RegularExpressions.Regex.Replace(BinwalkOracle.Scan(image), @"\s+", " ");
    Assert.That(scan, Does.Contain("TRX firmware image, version 1, partition count: 3, header size: 28 bytes, total size: " + image.Length + " bytes"));
  }
}
