using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class ExFatFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeExFatSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("ExFat");
    Assert.Multiple(() => {
      Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
      Assert.That(coverage.HasNativeReadinessProvider, Is.True);
    });
  }

  [Test]
  public void WriterBuiltNestedVolume_MountsNativelyAndReadsPositionally() {
    var expected = Enumerable.Range(0, 12_000).Select(i => (byte)(i * 37)).ToArray();
    var writer = new ExFatWriter();
    writer.AddFile("README.TXT", "root"u8.ToArray());
    writer.AddFile("DOCS/API/PAYLOAD.BIN", expected);
    using var image = new MemoryStream(writer.Build(16), writable: true);

    image.Position = 0;
    var profile = FormatRegistry.ProbeFilesystem("ExFat", image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.ProfileName, Does.Contain("native"));
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.RandomAccess), Is.True);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.StableNodeIds), Is.True);
    });

    image.Position = 0;
    using var fs = FormatRegistry.OpenFilesystem(
      "ExFat", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var root = fs.RootNodeId;
    var docs = fs.Lookup(root, "docs");
    Assert.That(docs.HasValue, Is.True, "exFAT lookup is case-insensitive while preserving the stored name");
    Assert.That(fs.Lookup(root, "DOCS"), Is.EqualTo(docs), "repeated lookup must preserve node identity");
    var api = fs.Lookup(docs!.Value, "api");
    Assert.That(api.HasValue, Is.True);
    var payloadId = fs.Lookup(api!.Value, "payload.bin");
    Assert.That(payloadId.HasValue, Is.True);

    using var handle = fs.OpenFile(payloadId!.Value, FileAccess.Read);
    var slice = new byte[513];
    Assert.That(handle.Read(4093, slice), Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(expected.AsSpan(4093, slice.Length).ToArray()).AsCollection);
  }

  [Test]
  public void NoFatChainAndValidDataLength_AreHonoredByReaderAndMountedHandle() {
    var payload = Enumerable.Range(0, 9000).Select(i => (byte)(i * 19 + 7)).ToArray();
    var writer = new ExFatWriter();
    writer.AddFile("PAD.BIN", payload);
    var bytes = writer.Build(16);

    var entryOffset = FindRootFileEntry(bytes, "PAD.BIN");
    var secondaryCount = bytes[entryOffset + 1];
    var setLength = (secondaryCount + 1) * 32;
    var streamOffset = entryOffset + 32;
    bytes[streamOffset + 1] |= 0x02; // GeneralSecondaryFlags.NoFatChain
    const long validLength = 4317;
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(streamOffset + 8, 8), validLength);
    RecomputeEntrySetChecksum(bytes.AsSpan(entryOffset, setLength));

    using var readerImage = new MemoryStream(bytes, writable: false);
    using var reader = new ExFatReader(readerImage, leaveOpen: true);
    var entry = reader.Entries.Single(e => e.Name == "PAD.BIN");
    var extracted = reader.Extract(entry);
    Assert.Multiple(() => {
      Assert.That(extracted.AsSpan(0, (int)validLength).ToArray(),
        Is.EqualTo(payload.AsSpan(0, (int)validLength).ToArray()).AsCollection);
      Assert.That(extracted.AsSpan((int)validLength).ContainsAnyExcept((byte)0), Is.False,
        "bytes above ValidDataLength must read as zero even when stale cluster bytes are non-zero");
    });

    using var mountedImage = new MemoryStream(bytes, writable: false);
    using var fs = FormatRegistry.OpenFilesystem(
      "ExFat", mountedImage, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var id = fs.Lookup(fs.RootNodeId, "pad.bin");
    Assert.That(id.HasValue, Is.True);
    using var handle = fs.OpenFile(id!.Value, FileAccess.Read);
    var acrossBoundary = new byte[64];
    Assert.That(handle.Read(validLength - 17, acrossBoundary), Is.EqualTo(acrossBoundary.Length));
    Assert.Multiple(() => {
      Assert.That(acrossBoundary.AsSpan(0, 17).ToArray(),
        Is.EqualTo(payload.AsSpan((int)validLength - 17, 17).ToArray()).AsCollection);
      Assert.That(acrossBoundary.AsSpan(17).ContainsAnyExcept((byte)0), Is.False);
    });
  }

  [Test]
  public void ProbeRejectsCorruptBootChecksum() {
    var bytes = new ExFatWriter().Build(16);
    var bytesPerSector = 1 << bytes[108];
    bytes[5 * bytesPerSector + 17] ^= 0x5A;

    using var image = new MemoryStream(bytes, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("ExFat", image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Limitations.Any(x => x.Contains("checksum", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }

  [Test]
  public void NonAsciiNameFallsBackWithoutLosingReadCompatibility() {
    var writer = new ExFatWriter();
    writer.AddFile("Ä.TXT", "unicode"u8.ToArray());
    using var image = new MemoryStream(writer.Build(16), writable: false);

    var profile = FormatRegistry.ProbeFilesystem("ExFat", image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.ProfileName, Does.Contain("derived"));
      Assert.That(profile.Limitations.Any(x => x.Contains("upcase", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }

  [Test]
  public void BlockDeviceProviderUsesTheSameNativeParser() {
    var writer = new ExFatWriter();
    writer.AddFile("HELLO.TXT", "hello"u8.ToArray());
    using var image = new MemoryStream(writer.Build(16), writable: false);
    using var device = new StreamBlockDevice(image, 512, writable: false, leaveOpen: true);
    var adapter = FormatRegistry.GetFilesystemDriver("ExFat");
    Assert.That(adapter, Is.AssignableTo<IBlockDeviceFilesystemDriverProvider>());

    var profile = ((IBlockDeviceFilesystemDriverProvider)adapter!).ProbeFilesystem(device);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.ProfileName, Does.Contain("native"));
  }

  [Test]
  public void WritableMountedOpenIsExplicitlyRejected() {
    using var image = new MemoryStream(new ExFatWriter().Build(16), writable: true);
    Assert.Throws<NotSupportedException>(() => FormatRegistry.OpenFilesystem(
      "ExFat", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }

  private static int FindRootFileEntry(byte[] image, string name) {
    var bytesPerSector = 1 << image[108];
    var sectorsPerCluster = 1 << image[109];
    var clusterSize = bytesPerSector * sectorsPerCluster;
    var heapSector = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(88, 4));
    var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(96, 4));
    var rootOffset = checked((int)((long)heapSector * bytesPerSector + (rootCluster - 2L) * clusterSize));

    for (var offset = rootOffset; offset < rootOffset + clusterSize; offset += 32) {
      if (image[offset] == 0x00) break;
      if (image[offset] != 0x85) continue;
      var secondaryCount = image[offset + 1];
      var setLength = (secondaryCount + 1) * 32;
      var set = image.AsSpan(offset, setLength);
      var streamOffset = 32;
      if (setLength < 64 || set[streamOffset] != 0xC0) continue;
      var nameLength = set[streamOffset + 3];
      if (DecodeName(set, nameLength) == name) return offset;
    }
    throw new AssertionException($"root file '{name}' was not found");
  }

  private static string DecodeName(ReadOnlySpan<byte> set, int nameLength) {
    var chars = new List<char>(nameLength);
    for (var index = 0; index < nameLength; ++index) {
      var nameEntry = 64 + index / 15 * 32;
      var charOffset = nameEntry + 2 + index % 15 * 2;
      chars.Add((char)BinaryPrimitives.ReadUInt16LittleEndian(set.Slice(charOffset, 2)));
    }
    return new string(chars.ToArray());
  }

  private static void RecomputeEntrySetChecksum(Span<byte> set) {
    set[2] = 0;
    set[3] = 0;
    ushort checksum = 0;
    for (var i = 0; i < set.Length; ++i) {
      if (i is 2 or 3) continue;
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]) & 0xFFFF);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(set.Slice(2, 2), checksum);
  }
}
