#pragma warning disable CS1591
#pragma warning disable CA5350 // Tests intentionally exercise Pak's mandated SHA-1 fields.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.UnrealPak;

namespace Compression.Tests.UnrealPak;

[TestFixture]
public class UnrealPakTests {
  [Test]
  public void Descriptor_ClaimsOnlyPak_AndWormCreate() {
    var descriptor = new UnrealPakFormatDescriptor();
    Assert.That(descriptor.Extensions, Is.EqualTo(new[] { ".pak" }));
    Assert.That(descriptor.Extensions, Does.Not.Contain(".utoc"));
    Assert.That(descriptor.Extensions, Does.Not.Contain(".ucas"));
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Writer_CreatesStoredV3_WithVerifiedIndexAndEntryHashes() {
    var payload = Encoding.UTF8.GetBytes("Hello, Unreal Pak v3!");
    var pak = CreatePak(
      [ArchiveInputInfo.InMemory("Content/test.uasset", payload)],
      new FormatCreateOptions { MethodName = "stored" });

    using var stream = new MemoryStream(pak, writable: false);
    var reader = new UnrealPakReader(stream);
    Assert.That(reader.PakVersion, Is.EqualTo(3u));
    Assert.That(reader.IndexHashVerified, Is.True);
    Assert.That(reader.IsIndexEncrypted, Is.False);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var entry = reader.Entries[0];
    Assert.That(entry.Path, Is.EqualTo("Content/test.uasset"));
    Assert.That(entry.CompressionMethod, Is.EqualTo(UnrealPakReader.CompressionNone));
    Assert.That(entry.Hash, Is.EqualTo(SHA1.HashData(payload)));
    Assert.DoesNotThrow(() => reader.VerifyEntry(entry));
    Assert.That(reader.Extract(entry), Is.EqualTo(payload));

    var footer = pak.AsSpan(pak.Length - 44);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(footer), Is.EqualTo(UnrealPakReader.Magic));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]), Is.EqualTo(3u));
    var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(footer[8..]);
    var indexSize = BinaryPrimitives.ReadInt64LittleEndian(footer[16..]);
    Assert.That(indexOffset, Is.EqualTo(reader.IndexOffset));
    Assert.That(indexSize, Is.EqualTo(reader.IndexSize));
    var rawIndex = pak.AsSpan(checked((int)indexOffset), checked((int)indexSize));
    Assert.That(footer.Slice(24, 20).ToArray(), Is.EqualTo(SHA1.HashData(rawIndex)));

    // Data-record FPakEntry offset is zero; the index copy stores the actual record offset.
    Assert.That(BinaryPrimitives.ReadInt64LittleEndian(pak.AsSpan(0, 8)), Is.Zero);
  }

  [Test]
  public void Writer_CreatesMultiBlockZlib_AndReaderDecodesEachBlock() {
    var payload = Enumerable.Range(0, 20_000)
      .Select(i => (byte)('A' + (i / 701) % 5))
      .ToArray();
    var pak = CreatePak(
      [ArchiveInputInfo.InMemory("Data/repeating.bin", payload)],
      new FormatCreateOptions {
        MethodName = "zlib",
        FormatSpecific = new Dictionary<string, string> { ["CompressionBlockSize"] = "4096" },
      });

    using var stream = new MemoryStream(pak, writable: false);
    var reader = new UnrealPakReader(stream);
    var entry = reader.Entries.Single();
    Assert.That(entry.CompressionMethod, Is.EqualTo(UnrealPakReader.CompressionZlib));
    Assert.That(entry.CompressionBlockSize, Is.EqualTo(4096u));
    Assert.That(entry.CompressionBlocks.Count, Is.GreaterThan(1));
    Assert.That(entry.CompressionBlocks.All(block => block.CompressedStart < block.CompressedEnd), Is.True);
    Assert.That(entry.CompressionBlocks.Zip(entry.CompressionBlocks.Skip(1), (left, right) => left.CompressedEnd <= right.CompressedStart).All(x => x), Is.True);
    Assert.DoesNotThrow(() => reader.VerifyEntry(entry));
    Assert.That(reader.Extract(entry), Is.EqualTo(payload));
  }

  [Test]
  public void Writer_AutoChoosesCompressionOnlyWhenWholePakEntryGetsSmaller() {
    var compressible = Enumerable.Repeat((byte)'Q', 32_000).ToArray();
    var random = new byte[32_000];
    new Random(12345).NextBytes(random);
    var pak = CreatePak(
      [
        ArchiveInputInfo.InMemory("compressible.bin", compressible),
        ArchiveInputInfo.InMemory("random.bin", random),
      ],
      new FormatCreateOptions { MethodName = "auto" });

    using var stream = new MemoryStream(pak, writable: false);
    var reader = new UnrealPakReader(stream);
    var compressibleEntry = reader.Entries.Single(entry => entry.Path == "compressible.bin");
    var randomEntry = reader.Entries.Single(entry => entry.Path == "random.bin");
    Assert.That(compressibleEntry.CompressionMethod, Is.EqualTo(UnrealPakReader.CompressionZlib));
    Assert.That(randomEntry.CompressionMethod, Is.EqualTo(UnrealPakReader.CompressionNone));
    Assert.That(reader.Extract(compressibleEntry), Is.EqualTo(compressible));
    Assert.That(reader.Extract(randomEntry), Is.EqualTo(random));
  }

  [Test]
  public void Writer_SupportsUnicodeFStrings_AndMountPoint() {
    var payload = Encoding.UTF8.GetBytes("unicode");
    var pak = CreatePak(
      [ArchiveInputInfo.InMemory("Daten/äöü/猫.txt", payload)],
      new FormatCreateOptions {
        MethodName = "stored",
        FormatSpecific = new Dictionary<string, string> { ["MountPoint"] = "../../../Spiel/" },
      });

    var descriptor = new UnrealPakFormatDescriptor();
    using var stream = new MemoryStream(pak, writable: false);
    var entries = descriptor.List(stream, null);
    Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] { "Spiel/Daten/äöü/猫.txt" }));
    stream.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(stream, "Spiel/Daten/äöü/猫.txt", null), Is.EqualTo(payload));
  }

  [Test]
  public void Reader_VerifiesRelativeCompressionBlocks_ForVersion5() {
    var dummy = Enumerable.Repeat((byte)0x2A, 137).ToArray();
    var payload = Enumerable.Repeat((byte)'R', 12_000).ToArray();
    var v3 = CreatePak(
      [
        ArchiveInputInfo.InMemory("00-dummy.bin", dummy),
        ArchiveInputInfo.InMemory("10-compressed.bin", payload),
      ],
      new FormatCreateOptions {
        MethodName = "zlib",
        FormatSpecific = new Dictionary<string, string> { ["CompressionBlockSize"] = "4096" },
        IncompressiblePaths = new HashSet<string>(StringComparer.Ordinal) { "00-dummy.bin" },
      });

    var v5 = ConvertV3ToV5RelativeOffsets(v3);
    using var stream = new MemoryStream(v5, writable: false);
    var reader = new UnrealPakReader(stream);
    Assert.That(reader.PakVersion, Is.EqualTo(5u));
    var entry = reader.Entries.Single(item => item.Path == "10-compressed.bin");
    Assert.That(entry.Offset, Is.GreaterThan(0));
    Assert.That(entry.CompressionBlocks, Has.Count.EqualTo(3));
    Assert.DoesNotThrow(() => reader.VerifyEntry(entry));
    Assert.That(reader.Extract(entry), Is.EqualTo(payload));
  }

  [Test]
  public void Reader_RejectsCorruptedIndexSha1() {
    var pak = CreatePak(
      [ArchiveInputInfo.InMemory("a.txt", "payload"u8.ToArray())],
      new FormatCreateOptions { MethodName = "stored" });
    long indexOffset;
    using (var valid = new MemoryStream(pak, writable: false))
      indexOffset = new UnrealPakReader(valid).IndexOffset;

    pak[checked((int)indexOffset) + 1] ^= 0x40;
    using var corrupted = new MemoryStream(pak, writable: false);
    var error = Assert.Throws<InvalidDataException>(() => _ = new UnrealPakReader(corrupted));
    Assert.That(error!.Message, Does.Contain("index SHA-1"));
  }

  [Test]
  public void Reader_RejectsCorruptedEntrySha1BeforeReturningData() {
    var payload = Encoding.UTF8.GetBytes("entry integrity payload");
    var pak = CreatePak(
      [ArchiveInputInfo.InMemory("a.txt", payload)],
      new FormatCreateOptions { MethodName = "stored" });

    // Pak v3 stored local entry header is 53 bytes.
    pak[53] ^= 0x01;
    using var stream = new MemoryStream(pak, writable: false);
    var reader = new UnrealPakReader(stream);
    var error = Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries.Single()));
    Assert.That(error!.Message, Does.Contain("SHA-1"));
  }

  [Test]
  public void Reader_RejectsModernV8InsteadOfGuessingItsIndexLayout() {
    var pak = CreatePak(
      [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())],
      new FormatCreateOptions { MethodName = "stored" });
    var footerOffset = pak.Length - 44;
    BinaryPrimitives.WriteUInt32LittleEndian(pak.AsSpan(footerOffset + 4, 4), 8u);

    using var stream = new MemoryStream(pak, writable: false);
    var error = Assert.Throws<NotSupportedException>(() => _ = new UnrealPakReader(stream));
    Assert.That(error!.Message, Does.Contain("v8+"));
  }

  [Test]
  public void Descriptor_RebuildBackedAddRemove_RoundTripsWithoutCanModifyClaim() {
    var descriptor = new UnrealPakFormatDescriptor();
    var initial = CreatePak(
      [ArchiveInputInfo.InMemory("old.txt", "old"u8.ToArray())],
      new FormatCreateOptions { MethodName = "stored" });
    using var archive = new MemoryStream();
    archive.Write(initial);
    archive.Position = 0;

    descriptor.Add(archive, [ArchiveInputInfo.InMemory("new.txt", "new"u8.ToArray())]);
    archive.Position = 0;
    Assert.That(descriptor.List(archive, null).Select(entry => entry.Name).Order(),
      Is.EqualTo(new[] { "new.txt", "old.txt" }));

    archive.Position = 0;
    descriptor.Remove(archive, ["old.txt"]);
    archive.Position = 0;
    Assert.That(descriptor.List(archive, null).Select(entry => entry.Name), Is.EqualTo(new[] { "new.txt" }));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "new.txt", null), Is.EqualTo("new"u8.ToArray()));
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
  }

  [Test]
  public void Writer_IsDeterministic_AndRejectsUnsafeOrEncryptedRequests() {
    var inputs = new[] {
      ArchiveInputInfo.InMemory("b.bin", Enumerable.Repeat((byte)'B', 5000).ToArray()),
      ArchiveInputInfo.InMemory("a.bin", Enumerable.Repeat((byte)'A', 5000).ToArray()),
    };
    var options = new FormatCreateOptions { MethodName = "auto", Optimize = true };
    Assert.That(CreatePak(inputs, options), Is.EqualTo(CreatePak(inputs.Reverse().ToArray(), options)));

    Assert.Throws<ArgumentException>(() => CreatePak(
      [ArchiveInputInfo.InMemory("../escape.txt", [] )], new FormatCreateOptions()));
    Assert.Throws<ArgumentException>(() => CreatePak(
      [ArchiveInputInfo.InMemory("same.txt", []), ArchiveInputInfo.InMemory("same.txt", [1])],
      new FormatCreateOptions()));
    Assert.Throws<NotSupportedException>(() => CreatePak(
      [ArchiveInputInfo.InMemory("a.txt", [])], new FormatCreateOptions { Password = "secret" }));
    Assert.Throws<NotSupportedException>(() => CreatePak(
      [ArchiveInputInfo.InMemory("a.txt", [])], new FormatCreateOptions { MethodName = "oodle" }));
  }

  private static byte[] CreatePak(IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var descriptor = new UnrealPakFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Create(output, inputs, options);
    return output.ToArray();
  }

  private static byte[] ConvertV3ToV5RelativeOffsets(byte[] source) {
    var bytes = source.ToArray();
    using var sourceStream = new MemoryStream(bytes, writable: false);
    var reader = new UnrealPakReader(sourceStream);
    Assert.That(reader.PakVersion, Is.EqualTo(3u));

    // Rewrite local compressed-block offsets from absolute to record-relative.
    foreach (var entry in reader.Entries.Where(entry => entry.CompressionMethod != UnrealPakReader.CompressionNone)) {
      var position = checked((int)entry.Offset + 48);
      var blockCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, 4));
      position += 4;
      for (var i = 0; i < blockCount; ++i) {
        var start = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(position, 8));
        var end = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(position + 8, 8));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(position, 8), start - entry.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(position + 8, 8), end - entry.Offset);
        position += 16;
      }
    }

    // Rewrite the same block table copies in the legacy index.
    var indexPosition = checked((int)reader.IndexOffset);
    SkipFString(bytes, ref indexPosition); // mount point
    var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(indexPosition, 4));
    indexPosition += 4;
    for (var fileIndex = 0; fileIndex < count; ++fileIndex) {
      SkipFString(bytes, ref indexPosition);
      var recordStart = indexPosition;
      var entryOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(recordStart, 8));
      var compressionMethod = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(recordStart + 24, 4));
      indexPosition = recordStart + 48;
      if (compressionMethod != UnrealPakReader.CompressionNone) {
        var blockCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(indexPosition, 4));
        indexPosition += 4;
        for (var i = 0; i < blockCount; ++i) {
          var start = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(indexPosition, 8));
          var end = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(indexPosition + 8, 8));
          BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(indexPosition, 8), start - entryOffset);
          BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(indexPosition + 8, 8), end - entryOffset);
          indexPosition += 16;
        }
      }
      indexPosition += 5; // flags + compression block size
    }

    // v5 prepends bEncryptedIndex before the stable 44-byte footer core.
    var oldFooter = bytes.Length - 44;
    var result = new byte[bytes.Length + 1];
    Array.Copy(bytes, 0, result, 0, oldFooter);
    result[oldFooter] = 0;
    Array.Copy(bytes, oldFooter, result, oldFooter + 1, 44);
    var magicOffset = oldFooter + 1;
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(magicOffset + 4, 4), 5u);

    var indexHash = SHA1.HashData(result.AsSpan(checked((int)reader.IndexOffset), checked((int)reader.IndexSize)));
    indexHash.CopyTo(result.AsSpan(magicOffset + 24, 20));
    return result;
  }

  private static void SkipFString(byte[] data, ref int position) {
    var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, 4));
    position += 4;
    if (length > 0)
      position += length;
    else if (length < 0)
      position += checked(-length * 2);
  }
}
