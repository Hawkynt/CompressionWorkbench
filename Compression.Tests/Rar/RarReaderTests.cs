using FileFormat.Rar;

namespace Compression.Tests.Rar;

[TestFixture]
public class RarReaderTests {
  [Category("HappyPath")]
  [Test]
  public void Signature_Detection_Rar5() {
    var ms = CreateStoreArchive("test.txt", [1, 2, 3]);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.IsRar5, Is.True);
  }

  [Category("HappyPath")]
  [Test]
  public void ReadEntries_SingleFile_Store() {
    var data = System.Text.Encoding.UTF8.GetBytes("Hello RAR!");
    var ms = CreateStoreArchive("hello.txt", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(data.Length));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_SingleFile_Store() {
    var data = System.Text.Encoding.UTF8.GetBytes("Hello RAR!");
    var ms = CreateStoreArchive("hello.txt", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_MultipleFiles_Store() {
    byte[] d1 = [1, 2, 3];
    byte[] d2 = [4, 5, 6, 7, 8];

    var ms = CreateMultiFileStoreArchive(
      ("a.bin", d1),
      ("b.bin", d2));
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_EmptyFile() {
    byte[] data = [];
    var ms = CreateStoreArchive("empty.txt", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries[0].Size, Is.EqualTo(0));
    Assert.That(reader.Extract(0), Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_Directory() {
    var ms = CreateDirectoryArchive("mydir");
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].IsDirectory, Is.True);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Vint_EncodeDecode_RoundTrips() {
    foreach (var value in new ulong[] {
          0, 1, 127, 128, 255, 256, 16383, 16384,
          1_000_000, ulong.MaxValue >> 8
        }) {
      var ms = new MemoryStream();
      RarVint.Write(ms, value);
      ms.Position = 0;
      var decoded = RarVint.Read(ms, out var bytesRead);
      Assert.That(decoded, Is.EqualTo(value), $"Failed for value {value}");
      Assert.That(bytesRead, Is.EqualTo(ms.Length));
    }
  }

  [Category("Boundary")]
  [Test]
  public void Vint_EncodedSize_IsCorrect() {
    Assert.That(RarVint.EncodedSize(0), Is.EqualTo(1));
    Assert.That(RarVint.EncodedSize(127), Is.EqualTo(1));
    Assert.That(RarVint.EncodedSize(128), Is.EqualTo(2));
    Assert.That(RarVint.EncodedSize(16383), Is.EqualTo(2));
    Assert.That(RarVint.EncodedSize(16384), Is.EqualTo(3));
  }

  [Category("HappyPath")]
  [Test]
  public void Header_CrcIsCorrect() {
    byte[] data = [42];
    var ms = CreateStoreArchive("t.bin", data);
    ms.Position = 0;

    // If CRC is wrong, reader would throw
    using var reader = new RarReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void Entry_ReportsCorrectSizes() {
    var data = new byte[1234];
    new Random(42).NextBytes(data);
    var ms = CreateStoreArchive("sized.bin", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries[0].Size, Is.EqualTo(1234));
    Assert.That(reader.Entries[0].CompressedSize, Is.EqualTo(1234)); // Store mode
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Entry_PreservesTimestamps() {
    var time = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    byte[] data = [1];
    var ms = CreateStoreArchiveWithTime("t.txt", data, time);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries[0].ModifiedTime, Is.Not.Null);
    Assert.That(reader.Entries[0].ModifiedTime!.Value.UtcDateTime,
      Is.EqualTo(time.UtcDateTime).Within(TimeSpan.FromSeconds(1)));
  }

  [Category("Exception")]
  [Test]
  public void Extract_CrcMismatch_Throws() {
    byte[] data = [1, 2, 3];
    var ms = CreateStoreArchive("bad.bin", data);

    // Corrupt the data area (last 3 bytes before end header)
    // Find the data and flip a byte
    var archiveBytes = ms.ToArray();
    // The data is somewhere after the file header. We'll corrupt it.
    // Find "bad.bin" in the archive to locate the file header, then the data follows.
    var nameIdx = -1;
    var nameBytes = System.Text.Encoding.UTF8.GetBytes("bad.bin");
    for (var i = 0; i < archiveBytes.Length - nameBytes.Length; ++i) {
      if (archiveBytes.AsSpan(i, nameBytes.Length).SequenceEqual(nameBytes)) {
        nameIdx = i;
        break;
      }
    }

    Assert.That(nameIdx, Is.GreaterThan(0), "Could not find filename in archive");

    // Data starts right after the filename in the header
    var dataStart = nameIdx + nameBytes.Length;
    archiveBytes[dataStart] ^= 0xFF; // flip first data byte

    var corruptMs = new MemoryStream(archiveBytes);
    using var reader = new RarReader(corruptMs);

    Assert.Throws<InvalidDataException>(() => reader.Extract(0));
  }

  [Category("Exception")]
  [Test]
  public void Extract_IndexOutOfRange_Throws() {
    byte[] data = [1];
    var ms = CreateStoreArchive("t.bin", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.Throws<ArgumentOutOfRangeException>(() => reader.Extract(1));
    Assert.Throws<ArgumentOutOfRangeException>(() => reader.Extract(-1));
  }

  [Category("HappyPath")]
  [Test]
  public void Rar4_Signature_Accepted() {
    var ms = new MemoryStream();
    ms.Write(RarConstants.Rar4Signature);
    ms.WriteByte(0); // pad to 8 bytes for read
    // Add end-of-archive marker so the reader terminates cleanly
    ms.WriteByte(0x00); ms.WriteByte(0x00); // CRC placeholder
    ms.WriteByte(RarConstants.Rar4TypeEnd);  // HEAD_TYPE = end
    ms.WriteByte(0x00); ms.WriteByte(0x40); // HEAD_FLAGS
    ms.WriteByte(0x07); ms.WriteByte(0x00); // HEAD_SIZE = 7
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.IsRar5, Is.False);
    Assert.That(reader.Entries, Has.Count.EqualTo(0));
  }

  [Category("Exception")]
  [Test]
  public void Invalid_Signature_Throws() {
    var ms = new MemoryStream([0, 0, 0, 0, 0, 0, 0, 0]);
    Assert.Throws<InvalidDataException>(() => _ = new RarReader(ms));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Vint_Span_ReadMatchesStreamRead() {
    foreach (var value in new ulong[] { 0, 42, 128, 65535, 1_000_000 }) {
      var ms = new MemoryStream();
      RarVint.Write(ms, value);
      var bytes = ms.ToArray();

      var fromStream = RarVint.Read(new MemoryStream(bytes), out var streamBytesRead);
      var fromSpan = RarVint.Read(bytes.AsSpan(), out var spanBytesRead);

      Assert.That(fromSpan, Is.EqualTo(fromStream));
      Assert.That(spanBytesRead, Is.EqualTo(streamBytesRead));
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_LargerFile_Store() {
    // Test with a larger file to exercise the buffer loop
    var data = new byte[32768];
    new Random(123).NextBytes(data);
    var ms = CreateStoreArchive("large.bin", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Entry_CompressionMethod_IsStore() {
    byte[] data = [10, 20, 30];
    var ms = CreateStoreArchive("test.bin", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(RarConstants.MethodStore));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_ToOutputStream() {
    var data = System.Text.Encoding.UTF8.GetBytes("stream output test");
    var ms = CreateStoreArchive("stream.txt", data);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    using var output = new MemoryStream();
    reader.Extract(0, output);

    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Entry_IsSolid_ParsedFromCompressionInfo() {
    // Create an archive where the second file has the solid flag set
    byte[] d1 = [1, 2, 3];
    byte[] d2 = [4, 5, 6];

    var ms = new MemoryStream();
    ms.Write(RarConstants.Rar5Signature);
    WriteMainHeader(ms);
    WriteFileHeader(ms, "first.bin", d1, isDirectory: false, solid: false);
    WriteFileHeader(ms, "second.bin", d2, isDirectory: false, solid: true);
    WriteEndHeader(ms);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Entries[0].IsSolid, Is.False);
    Assert.That(reader.Entries[1].IsSolid, Is.True);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_SolidStore_ExtractsCorrectly() {
    // Solid Store entries should still extract correctly (Store bypasses decoder)
    byte[] d1 = [10, 20, 30];
    byte[] d2 = [40, 50, 60, 70];

    var ms = new MemoryStream();
    ms.Write(RarConstants.Rar5Signature);
    WriteMainHeader(ms);
    WriteFileHeader(ms, "a.bin", d1, isDirectory: false, solid: false);
    WriteFileHeader(ms, "b.bin", d2, isDirectory: false, solid: true);
    WriteEndHeader(ms);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Extract_NonSolid_ResetsDecoder() {
    // When a file is NOT solid, extraction starts fresh even after a solid file
    byte[] d1 = [1];
    byte[] d2 = [2];
    byte[] d3 = [3];

    var ms = new MemoryStream();
    ms.Write(RarConstants.Rar5Signature);
    WriteMainHeader(ms);
    WriteFileHeader(ms, "a.bin", d1, isDirectory: false, solid: false);
    WriteFileHeader(ms, "b.bin", d2, isDirectory: false, solid: true);
    WriteFileHeader(ms, "c.bin", d3, isDirectory: false, solid: false); // resets
    WriteEndHeader(ms);
    ms.Position = 0;

    using var reader = new RarReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
    Assert.That(reader.Extract(2), Is.EqualTo(d3));
  }

  // --- Helper methods: create RAR5 archives in memory ---

  private static MemoryStream CreateStoreArchive(string fileName, byte[] data) =>
    CreateMultiFileStoreArchive((fileName, data));

  private static MemoryStream CreateStoreArchiveWithTime(string fileName, byte[] data, DateTimeOffset time) =>
    CreateMultiFileStoreArchiveWithTime(time, (fileName, data));

  private static MemoryStream CreateDirectoryArchive(string dirName) {
    var ms = new MemoryStream();

    // Write RAR5 signature
    ms.Write(RarConstants.Rar5Signature);

    // Write main archive header
    WriteMainHeader(ms);

    // Write directory entry (no data)
    WriteFileHeader(ms, dirName, ReadOnlySpan<byte>.Empty, isDirectory: true);

    // Write end-of-archive header
    WriteEndHeader(ms);

    return ms;
  }

  private static MemoryStream CreateMultiFileStoreArchive(params (string Name, byte[] Data)[] files) {
    return CreateMultiFileStoreArchiveWithTime(null, files);
  }

  private static MemoryStream CreateMultiFileStoreArchiveWithTime(
    DateTimeOffset? time, params (string Name, byte[] Data)[] files) {
    var ms = new MemoryStream();

    // Write RAR5 signature
    ms.Write(RarConstants.Rar5Signature);

    // Write main archive header
    WriteMainHeader(ms);

    // Write file entries
    foreach (var (name, data) in files)
      WriteFileHeader(ms, name, data, isDirectory: false, mtime: time);

    // Write end-of-archive header
    WriteEndHeader(ms);

    return ms;
  }

  private static void WriteMainHeader(MemoryStream ms) {
    // Build header body: type=1, flags=0, archive flags=0
    var headerBody = new MemoryStream();
    RarVint.Write(headerBody, RarConstants.HeaderTypeMain); // type
    RarVint.Write(headerBody, 0);                           // header flags
    RarVint.Write(headerBody, 0);                           // archive flags

    WriteHeaderWithCrc(ms, headerBody.ToArray());
  }

  private static void WriteFileHeader(MemoryStream ms, string fileName, ReadOnlySpan<byte> data,
    bool isDirectory, DateTimeOffset? mtime = null, bool solid = false) {
    var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
    var crc = data.Length > 0 ? Compression.Core.Checksums.Crc32.Compute(data) : 0;

    var headerBody = new MemoryStream();
    RarVint.Write(headerBody, RarConstants.HeaderTypeFile); // type

    // Header flags
    var headerFlags = 0;
    if (!isDirectory && data.Length > 0)
      headerFlags |= RarConstants.HeaderFlagDataArea;
    RarVint.Write(headerBody, (ulong)headerFlags);

    // Data area size (if flagged)
    if ((headerFlags & RarConstants.HeaderFlagDataArea) != 0)
      RarVint.Write(headerBody, (ulong)data.Length);

    // File flags
    var fileFlags = 0;
    if (isDirectory) fileFlags |= RarConstants.FileFlagDirectory;
    if (mtime.HasValue) fileFlags |= RarConstants.FileFlagTimeMtime;
    if (data.Length > 0) fileFlags |= RarConstants.FileFlagCrc32;
    RarVint.Write(headerBody, (ulong)fileFlags);

    // Unpacked size
    RarVint.Write(headerBody, (ulong)data.Length);

    // Attributes
    RarVint.Write(headerBody, 0);

    // Mtime (4 bytes, uint32, Unix timestamp) if flagged
    if (mtime.HasValue) {
      var unixTime = (uint)mtime.Value.ToUnixTimeSeconds();
      headerBody.Write(BitConverter.GetBytes(unixTime));
    }

    // Data CRC if flagged
    if ((fileFlags & RarConstants.FileFlagCrc32) != 0)
      headerBody.Write(BitConverter.GetBytes(crc));

    // Compression info: version(bits 0-5)=0, solid(bit 6), method(bits 7-9)=Store(0), dictSizeLog(bits 10-13)=0
    var compressionInfo = solid ? 0x40 : 0;
    RarVint.Write(headerBody, (ulong)compressionInfo);

    // Host OS
    RarVint.Write(headerBody, RarConstants.OsWindows);

    // Name length + name
    RarVint.Write(headerBody, (ulong)nameBytes.Length);
    headerBody.Write(nameBytes);

    WriteHeaderWithCrc(ms, headerBody.ToArray());

    // Write data area
    if (data.Length > 0)
      ms.Write(data);
  }

  private static void WriteEndHeader(MemoryStream ms) {
    var headerBody = new MemoryStream();
    RarVint.Write(headerBody, RarConstants.HeaderTypeEndArchive); // type
    RarVint.Write(headerBody, 0);                                 // header flags
    RarVint.Write(headerBody, 0);                                 // end archive flags

    WriteHeaderWithCrc(ms, headerBody.ToArray());
  }

  private static void WriteHeaderWithCrc(MemoryStream ms, byte[] headerBody) {
    // RAR5 header layout:
    //   [Header CRC (4 bytes LE)] [Header size (vint)] [body...]
    // Header CRC = CRC-32 of (size vint bytes + body bytes)
    // Header size = body.Length (bytes from Type field to end of header)

    var sizeMs = new MemoryStream();
    RarVint.Write(sizeMs, (ulong)headerBody.Length);
    var sizeBytes = sizeMs.ToArray();

    // CRC covers sizeBytes + headerBody
    var crcData = new byte[sizeBytes.Length + headerBody.Length];
    Array.Copy(sizeBytes, 0, crcData, 0, sizeBytes.Length);
    Array.Copy(headerBody, 0, crcData, sizeBytes.Length, headerBody.Length);

    var headerCrc = Compression.Core.Checksums.Crc32.Compute(crcData);

    ms.Write(BitConverter.GetBytes(headerCrc));
    ms.Write(crcData);
  }
}
