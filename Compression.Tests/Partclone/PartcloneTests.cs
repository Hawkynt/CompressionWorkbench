using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Partclone;

namespace Compression.Tests.Partclone;

[TestFixture]
public class PartcloneTests {

  // ── Reference material ────────────────────────────────────────────────
  // Produced in WSL Ubuntu 24.04 with partclone 0.3.27 (Debian 0.3.27+repack-2build2):
  //   mkfs.fat -C fat.img 256; mcopy -i fat.img a.txt ::A.TXT   (a.txt = 300 × "hello partclone oracle\n")
  //   partclone.fat -c            -s fat.img -o crc.pcl    CRC32, 2048 blocks per checksum, reseed
  //   partclone.fat -c -a0        -s fat.img -o none.pcl   no checksum
  //   partclone.fat -c -k 3 -K    -s fat.img -o k3.pcl     CRC32, 3 blocks per checksum, no reseed
  // Stored gzip-compressed and base64-encoded.
  private const string FatImgGz = "H4sIAAAAAAAC/+3ZsWoUQRjA8cmpBE4TUgna+GFns2BqCV6hndHiFEEibJI9c9x6e+5uc2Chb5DnEEu7gPgC9xZ216SwSOW5EiysBUHz+83CN/ynmyl3ee/49WTUZKO8Tb3La6n3c52tpa1u/vIu3SnX35/sPo7dwaMH0Xk4GN7d7ubmrc8v3n68/aW9+uzT5sl6Wmy9XJ5uf11cX9xYfh8ejZvovmnVRh77VdXm+2URh+NmkkU8KYu8KWI8bYr6t/NRWc1m88inhxv9WV00Tbedx6SYR1tFW3cnr/LxNLIsi41+4k88/XC2Wl26n66crlzGheT9L7ZBnBs+H0ZK197s7O3snc9e+nbT/QAAAAAAAAAAAAAAAAAAAMDfcFSUZRWzvG4PympaRFXnB2XRl2VZlmVZlmVZlmVZlmVZlmVZluV/JvvrBwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/tx/RNfthAAAEAA==";
  private const string CrcPclGz = "H4sIAAAAAAAC/+3cP0sbcRjA8SfREkmriIOgiw/dHDxiOrhoMINu/hnSUlos/NRf9Mh5d97dEhDUd+DgC3ATxFEnQXwDeQu2Q7csDh2cGi9GQVcFQfL93MFzfO8H9ydj4EITJWte4NsJd8tsWCk4X5zilDwoFArF66v5cmWyKE/1dkb2YZU8n+0+dD9mRdtr+0QymcOj8z+ttl15qb2Z3MGP5vTBVq0aO1WTSLY3k14l3W4zMvh4O+2FMu7l9i8Wl3SxvDCnqftnSOfA2OXPndPPV8nHb2cDFzlpDP5q3hT/NoYbI83/lU031nT3g0SNrgZBYlY9q+tuXHNUlz1rYquuH9vo2fmqF4RhXY2/3p8PIxvH6WFda7auSaBJlJ7ZMK6vjuNof17wGl9Pblutnln5cNPiZXQlfv/uVtaOyveKinzaLq2UVjozK/9GeT8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAALyFTet5gYaP363RIDJrns2TyWQymUwmk8lkMplMJpPJZDKZTH7HmX9Autvv8eO9O7ND7ZS2YAAA";
  private const string NonePclGz = "H4sIAAAAAAAC/+3cvUrDUBiA4dOqFKoVcRDUwQ83B0Otg4uKHXTzZ6giisKpnmpoTGKSpSCod+B1iKObIN5AFy/ASVy6ODg4WdPWiq4KgvR9GvjKe05Jmo6B+jqI9hzPNZP2kT4wKmtNW7kZ9SGbzeYe7pbyhamc+qq7NZIfu9T32eiDzbHw+YlE4vTpfrTecKp+6mwudbFVm704KpdCq6QjlexOxGeJX68JNdC+nMZGNeGkzm9WVmUlv7woseZ3iGf/2O32ydX4XdS7cd1/k1LVgd3ac+6xOlQdrr0VDu1Q4sP1ItFS9LxIFx0j+3ZYtkTWHKNDI7YbmuDbesnxfL8i2t3PpP3AhGH8tiJlU5HIkyiIVw607YplWZJJK/zG+uVrvd61oHqe69yMjsTv39ny0lLYLIhSfcfzO/M7rZlULyPcHwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/sKhcRxP/Pb/1ogX6D3HpMlkMplMJpPJZDKZTCaTyWQymUwm/+PME5DO9g63n7husmAAAA==";
  private const string K3PclGz = "H4sIAAAAAAAC/+3cP2gTYRyH8TdpJBpNKQ5qdfCHoFLEI8ZBFC2NYhXB1j9RtFLh2r5pj1xzZ+6WgNBWHFyEgotTcVOKk+hiq9ZJELI7iC7qYEADKnQQ46Vpwa66+T6fBN7w5CDJN2Mgvl0Oh12vpPc64/aoVhlrv5U9oJZlMpnsu4XeXH5fVv0p0Triy1ep1Wezb1w6epRE17Y1W+zpg1P9jaYJ9bcmjySnB2qHp8eLhcAq2KGKJ2LRq0S3xZjqWHk7zQtVl5ucmuvrl77c6eMSWfoM0dm+/fmV6w93LITrLz5un0uqasfVWj37obqp2ln7lR9zAonuJS8UW4Y8L7SHXC0jTlC0RM642g60OKVAl1c9X3A936+IXRpJp/yyDoLoYUWKuiKhJ2E5embUdkpiWZakUwr/4sLsYqPR1qPW1BuMYSS+f7PdStcP5aQlfykvSm241j3YPdg64+r7VjYCgP/Va/VlMysAgJl+TD25ywoAYKaz+pXPCgBgpvm1686xAgCY6fKebcdYAQDM9Gni6A1WAAAz3Tn/8RErAICZbv98e5AVAMBMO2cq91gBAMz0ZtfMCVYAzDWmXdcTf+U/bsQr28OuTpENybUtL5+xC5lMJpuZe08mvrILmUwmm5lvdn6eZxcymUw2M8/u/vaCXchkczO/iJjtfdf9yd/rrA7V8mAAAA==";

  private static byte[] Gunzip(string b64) {
    using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static IEnumerable<TestCaseData> ReferenceImages() {
    yield return new TestCaseData(CrcPclGz, (ushort)0x20, 2048u, (byte)1).SetName("Reference_Crc32_Strip2048_Reseed");
    yield return new TestCaseData(NonePclGz, (ushort)0, 0u, (byte)1).SetName("Reference_NoChecksum");
    yield return new TestCaseData(K3PclGz, (ushort)0x20, 3u, (byte)0).SetName("Reference_Crc32_Strip3_NoReseed");
  }

  // ── Reading what partclone wrote ──────────────────────────────────────

  [TestCaseSource(nameof(ReferenceImages))]
  public void Reader_GivenPartcloneImage_WhenParsed_ThenHeaderFieldsMatchPartcloneInfo(string gz, ushort mode, uint strip, byte reseed) {
    using var ms = new MemoryStream(Gunzip(gz));
    var info = new PartcloneReader(ms).Info;
    Assert.Multiple(() => {
      Assert.That(info.PtcVersion, Is.EqualTo("0.3.27"));
      Assert.That(info.FsType, Is.EqualTo("FAT12"));
      Assert.That(info.BlockSize, Is.EqualTo(512u));
      Assert.That(info.TotalBlocks, Is.EqualTo(512UL));
      Assert.That(info.UsedBlocks, Is.EqualTo(48UL));
      Assert.That(info.SuperBlockUsedBlocks, Is.EqualTo(48UL));
      Assert.That(info.DeviceSize, Is.EqualTo(262144UL));
      Assert.That(info.ImageVersion, Is.EqualTo((ushort)2));
      Assert.That(info.CpuBits, Is.EqualTo((ushort)64));
      Assert.That(info.ChecksumMode, Is.EqualTo(mode));
      Assert.That(info.BlocksPerChecksum, Is.EqualTo(strip));
      Assert.That(info.ReseedChecksum, Is.EqualTo(reseed));
      Assert.That(info.BitmapMode, Is.EqualTo(PartcloneReader.BmBit));
      Assert.That(info.BitmapOffset, Is.EqualTo(110));
      Assert.That(info.DataOffset, Is.EqualTo(110 + 64 + 4));
    });
  }

  [TestCaseSource(nameof(ReferenceImages))]
  public void Reader_GivenPartcloneImage_WhenReconstructed_ThenEqualsSourcePartition(string gz, ushort mode, uint strip, byte reseed) {
    // partclone 0.3.27's FAT12 map leaves out blocks 47-48 (the tail of A.TXT) and keeps block
    // 511, so the expected partition is the source with every block outside that map zeroed -
    // which is also what partclone.restore produces from these images.
    using var ms = new MemoryStream(Gunzip(gz));
    var reader = new PartcloneReader(ms);
    var map = reader.ReadAllocationMap();
    var expected = Gunzip(FatImgGz);
    for (var b = 0; b < 512; ++b)
      if ((map[b >> 3] & (1 << (b & 7))) == 0) expected.AsSpan(b * 512, 512).Clear();
    Assert.That(reader.ReconstructDisk(), Is.EqualTo(expected));
  }

  // ── Writing what partclone writes ─────────────────────────────────────

  [TestCaseSource(nameof(ReferenceImages))]
  public void Writer_GivenExtractedEntries_WhenRecreated_ThenBytesEqualPartcloneOutput(string gz, ushort mode, uint strip, byte reseed) {
    var original = Gunzip(gz);
    var tmp = Path.Combine(Path.GetTempPath(), "pc_rt_" + Guid.NewGuid().ToString("N"));
    try {
      using (var src = new MemoryStream(original))
        new PartcloneFormatDescriptor().Extract(src, tmp, null, null);
      var inputs = new[] { "image.img", "metadata.ini", "allocation.map" }
        .Select(n => ArchiveInputInfo.InMemory(n, File.ReadAllBytes(Path.Combine(tmp, n)))).ToList();
      using var rebuilt = new MemoryStream();
      new PartcloneFormatDescriptor().Create(rebuilt, inputs, new FormatCreateOptions());
      Assert.That(rebuilt.ToArray(), Is.EqualTo(original));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Writer_GivenPartitionAndPartcloneMap_WhenWritten_ThenMatchesPartcloneByteForByte() {
    var reference = Gunzip(CrcPclGz);
    using var refStream = new MemoryStream(reference);
    var map = new PartcloneReader(refStream).ReadAllocationMap();
    var metadata = "fs = FAT12\nblock_size = 512\nsuperblock_used_blocks = 48\n"u8.ToArray();
    using var disk = new MemoryStream(Gunzip(FatImgGz));
    using var output = new MemoryStream();
    PartcloneWriter.Write(output, disk, metadata, map);
    Assert.That(output.ToArray(), Is.EqualTo(reference));
  }

  // ── Integrity checks (equivalence classes of damage) ──────────────────

  [TestCase(40, TestName = "Corrupt_HeaderFsField")]
  [TestCase(107, TestName = "Corrupt_HeaderCrc")]
  public void Reader_GivenDamagedDescriptor_WhenOpened_ThenRejected(int offset) {
    var image = Gunzip(CrcPclGz);
    image[offset] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => _ = new PartcloneReader(new MemoryStream(image)));
  }

  [TestCase(120, TestName = "Corrupt_Bitmap")]
  [TestCase(175, TestName = "Corrupt_BitmapCrc")]
  [TestCase(178 + 600, TestName = "Corrupt_DataBlock")]
  public void Reader_GivenDamagedBody_WhenReconstructed_ThenRejected(int offset) {
    var image = Gunzip(CrcPclGz);
    image[offset] ^= 0x01;
    var reader = new PartcloneReader(new MemoryStream(image));
    Assert.Throws<InvalidDataException>(() => reader.ReconstructDisk());
  }

  [Test]
  public void Reader_GivenDamagedMidStripChecksum_WhenReconstructed_ThenRejected() {
    var image = Gunzip(K3PclGz);
    var firstChecksum = 178 + 3 * 512;
    image[firstChecksum] ^= 0x80;
    Assert.Throws<InvalidDataException>(() => new PartcloneReader(new MemoryStream(image)).ReconstructDisk());
  }

  [Test]
  public void Reader_GivenTruncatedData_WhenReconstructed_ThenEndOfStream() {
    var image = Gunzip(CrcPclGz);
    var reader = new PartcloneReader(new MemoryStream(image.AsSpan(0, image.Length - 10).ToArray()));
    Assert.Throws<EndOfStreamException>(() => reader.ReconstructDisk());
  }

  [TestCase(0, TestName = "Truncated_Empty")]
  [TestCase(35, TestName = "Truncated_InsideHead")]
  [TestCase(109, TestName = "Truncated_BeforeCrcEnd")]
  public void Reader_GivenTruncatedDescriptor_WhenOpened_ThenRejected(int length) {
    var image = Gunzip(CrcPclGz).AsSpan(0, length).ToArray();
    Assert.Throws<InvalidDataException>(() => _ = new PartcloneReader(new MemoryStream(image)));
  }

  [Test]
  public void Reader_GivenFormat0001_WhenOpened_ThenNotSupported() {
    var image = Gunzip(CrcPclGz);
    "0001"u8.CopyTo(image.AsSpan(30));
    Assert.Throws<NotSupportedException>(() => _ = new PartcloneReader(new MemoryStream(image)));
  }

  [Test]
  public void Reader_GivenBigEndianMarker_WhenOpened_ThenNotSupported() {
    var image = Gunzip(CrcPclGz);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(34), 0xDEC0);
    Assert.Throws<NotSupportedException>(() => _ = new PartcloneReader(new MemoryStream(image)));
  }

  [Test]
  public void Reader_GivenBadMagic_WhenOpened_ThenRejected() {
    var image = Gunzip(CrcPclGz);
    image[0] = (byte)'X';
    Assert.Throws<InvalidDataException>(() => _ = new PartcloneReader(new MemoryStream(image)));
  }

  // ── Writer options, read back by our reader ───────────────────────────

  [TestCase("1", "1", "256", "1")]
  [TestCase("1", "crc32", "1", "1")]
  [TestCase("bit", "1", "7", "0")]
  [TestCase("1", "none", "0", "1")]
  [TestCase("0", "1", "4", "1")]
  [TestCase("none", "32", "13", "1")]
  [TestCase("1", "1", "14", "1")]
  public void Writer_GivenOptions_WhenRoundTripped_ThenPartitionAndAllocationSurvive(string bitmap, string checksum, string strip, string reseed) {
    var raw = new byte[13 * 64];
    raw.AsSpan(64, 64).Fill(0x5A);
    raw.AsSpan(12 * 64, 64).Fill(0xC3);
    // Block 0 is allocated but all-zero; block 5 is free but not zero.
    raw.AsSpan(5 * 64, 64).Fill(0x11);
    byte[] map = [0b0000_0011, 0b0001_0000]; // blocks 0, 1, 12
    var options = new Dictionary<string, string> {
      ["BitmapMode"] = bitmap, ["ChecksumMode"] = checksum, ["BlocksPerChecksum"] = strip, ["ReseedChecksum"] = reseed,
    };
    using var output = new MemoryStream();
    PartcloneWriter.Write(output, new MemoryStream(raw), "block_size = 64\nbitmap_mode = 1\n"u8, map, options);

    output.Position = 0;
    var reader = new PartcloneReader(output);
    var expected = (byte[])raw.Clone();
    var mapped = bitmap is not ("0" or "none");
    if (mapped) expected.AsSpan(5 * 64, 64).Clear(); // a free block is not stored
    Assert.That(reader.ReconstructDisk(), Is.EqualTo(expected));
    Assert.That(reader.Info.UsedBlocks, Is.EqualTo(mapped ? 3UL : 13UL));
    Assert.That(reader.Info.BitmapMode, Is.EqualTo(bitmap is "0" or "none" ? PartcloneReader.BmNone : PartcloneReader.BmBit));
  }

  [TestCase("BitmapMode", "8", TestName = "Refuse_ByteMap_PartcloneCannotRestoreIt")]
  [TestCase("BitmapMode", "byte", TestName = "Refuse_ByteMap_ByName")]
  [TestCase("ChecksumMode", "xxh64", TestName = "Refuse_Xxh64_Unverifiable")]
  [TestCase("ChecksumMode", "3", TestName = "Refuse_UnknownChecksum")]
  public void Writer_GivenModePartcloneCannotConfirm_WhenWritten_ThenNotSupported(string key, string value) {
    Assert.Throws<NotSupportedException>(() => PartcloneWriter.Write(new MemoryStream(), new MemoryStream(new byte[64]),
      "block_size = 16\n"u8, [], new Dictionary<string, string> { [key] = value }));
  }

  [TestCase(true, TestName = "ByteMap_WithBitMagicTrailer")]
  [TestCase(false, TestName = "ByteMap_WithCrcTrailer")]
  public void Reader_GivenByteMapImage_WhenReconstructed_ThenEitherTrailerAccepted(bool magicTrailer) {
    // Derive a byte-map image from partclone's bit-map image: same blocks, map widened to bytes.
    var bitImage = Gunzip(NonePclGz);
    var bits = bitImage.AsSpan(110, 64).ToArray();
    var header = bitImage.AsSpan(0, 110).ToArray();
    header[105] = PartcloneReader.BmByte;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(106), PartcloneReader.PartcloneCrc32(header.AsSpan(0, 106)));
    using var ms = new MemoryStream();
    ms.Write(header);
    for (var b = 0; b < 512; ++b) ms.WriteByte((byte)((bits[b >> 3] >> (b & 7)) & 1));
    if (magicTrailer) ms.Write("BiTmAgIc"u8);
    else ms.Write(BitConverter.GetBytes(PartcloneReader.PartcloneCrc32(bits)));
    ms.Write(bitImage.AsSpan(110 + 64 + 4));

    ms.Position = 0;
    var fromBytes = new PartcloneReader(ms).ReconstructDisk();
    Assert.That(fromBytes, Is.EqualTo(new PartcloneReader(new MemoryStream(bitImage)).ReconstructDisk()));
  }

  [Test]
  public void Writer_GivenNoAllocationMap_WhenWritten_ThenOnlyNonZeroBlocksAreStored() {
    var raw = new byte[4 * 16];
    raw.AsSpan(16, 16).Fill(0xAA);
    using var output = new MemoryStream();
    PartcloneWriter.Write(output, new MemoryStream(raw), "block_size = 16\n"u8, []);
    output.Position = 0;
    var reader = new PartcloneReader(output);
    Assert.That(reader.Info.UsedBlocks, Is.EqualTo(1UL));
    Assert.That(reader.ReadAllocationMap(), Is.EqualTo(new byte[] { 0b0010 }));
    Assert.That(reader.ReconstructDisk(), Is.EqualTo(raw));
  }

  [Test]
  public void Writer_GivenMapWithBitsPastLastBlock_WhenWritten_ThenPaddingBitsIgnored() {
    var raw = new byte[3 * 16];
    raw.AsSpan(0, 16).Fill(1);
    using var output = new MemoryStream();
    PartcloneWriter.Write(output, new MemoryStream(raw), "block_size = 16\n"u8, [0xF9]);
    output.Position = 0;
    var reader = new PartcloneReader(output);
    Assert.That(reader.Info.UsedBlocks, Is.EqualTo(1UL));
    Assert.That(reader.ReconstructDisk(), Is.EqualTo(raw));
  }

  [TestCase(63, TestName = "Geometry_LengthNotMultipleOfBlock")]
  [TestCase(0, TestName = "Geometry_Empty")]
  public void Writer_GivenPartitionNotAWholeNumberOfBlocks_WhenWritten_ThenRefused(int length) {
    Assert.Throws<ArgumentException>(() =>
      PartcloneWriter.Write(new MemoryStream(), new MemoryStream(new byte[length]), "block_size = 16\n"u8, []));
  }

  [Test]
  public void Writer_GivenMapOfWrongLength_WhenWritten_ThenRefused() {
    Assert.Throws<ArgumentException>(() =>
      PartcloneWriter.Write(new MemoryStream(), new MemoryStream(new byte[64]), "block_size = 16\nbitmap_mode = 1\n"u8, [1, 2]));
  }

  // ── Descriptor surface ────────────────────────────────────────────────

  [Test]
  public void Descriptor_GivenReferenceImage_WhenListed_ThenMetadataMapAndImage() {
    using var ms = new MemoryStream(Gunzip(CrcPclGz));
    var entries = new PartcloneFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "allocation.map", "image.img" }));
    Assert.That(entries[1].OriginalSize, Is.EqualTo(64));
    Assert.That(entries[2].OriginalSize, Is.EqualTo(262144));
    Assert.That(entries[2].CompressedSize, Is.EqualTo(48 * 512));
  }

  [Test]
  public void Descriptor_GivenDamagedDescriptor_WhenListed_ThenPartialMetadataInsteadOfThrow() {
    var image = Gunzip(CrcPclGz);
    image[107] ^= 1;
    var entries = new PartcloneFormatDescriptor().List(new MemoryStream(image), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
    var tmp = Path.Combine(Path.GetTempPath(), "pc_bad_" + Guid.NewGuid().ToString("N"));
    try {
      new PartcloneFormatDescriptor().Extract(new MemoryStream(image), tmp, null, ["metadata.ini"]);
      Assert.That(File.ReadAllText(Path.Combine(tmp, "metadata.ini")), Does.Contain("parse_status = partial"));
      Assert.Throws<InvalidDataException>(() => new PartcloneFormatDescriptor().Extract(new MemoryStream(image), tmp, null, null));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Descriptor_GivenMetadataFilter_WhenExtracted_ThenImageNotWritten() {
    var tmp = Path.Combine(Path.GetTempPath(), "pc_meta_" + Guid.NewGuid().ToString("N"));
    try {
      new PartcloneFormatDescriptor().Extract(new MemoryStream(Gunzip(CrcPclGz)), tmp, null, ["metadata.ini"]);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "image.img")), Is.False);
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [TestCase("PROBE.TXT", TestName = "Create_ForeignName")]
  [TestCase("metadata.ini", TestName = "Create_MissingImage")]
  public void Descriptor_GivenInputsWithoutPartitionImage_WhenCreating_ThenRefusedAsArgument(string name) {
    Assert.Throws<ArgumentException>(() => new PartcloneFormatDescriptor().Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory(name, "x"u8.ToArray())], new FormatCreateOptions()));
  }

  [Test]
  public void Descriptor_GivenOnlyImage_WhenCreated_ThenDefaultsToCrc32BitmapImage() {
    var raw = new byte[8 * 4096];
    raw.AsSpan(4096, 10).Fill(7);
    using var output = new MemoryStream();
    new PartcloneFormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("image.img", raw)], new FormatCreateOptions());
    output.Position = 0;
    var reader = new PartcloneReader(output);
    Assert.That(reader.Info.FsType, Is.EqualTo("raw"));
    Assert.That(reader.Info.ChecksumMode, Is.EqualTo(PartcloneReader.CsCrc32));
    Assert.That(reader.Info.BlocksPerChecksum, Is.EqualTo(256u)); // 1 MiB buffer / 4 KiB blocks
    Assert.That(reader.ReconstructDisk(), Is.EqualTo(raw));
  }

  [Test]
  public void LooksLikePartclone_GivenMagicOrNoise_ThenDistinguishes() {
    Assert.That(PartcloneReader.LooksLikePartclone(Gunzip(CrcPclGz)), Is.True);
    Assert.That(PartcloneReader.LooksLikePartclone(new byte[64]), Is.False);
    Assert.That(PartcloneReader.LooksLikePartclone("partclone-imag"u8), Is.False);
  }

  [Test]
  public void Descriptor_AdvertisesPartcloneMagic() {
    var d = new PartcloneFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Partclone"));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(PartcloneReader.Magic));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }
}
