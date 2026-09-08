using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class FatFilesystemDriverTests {
  [OneTimeSetUp]
  public void InitializeRegistry() => FormatRegistration.EnsureInitialized();

  [Test, Category("Driver")]
  public void GeneratedSidecar_IsRegisteredForFat() {
    var driver = FormatRegistry.GetFilesystemDriver("Fat");
    Assert.That(driver, Is.TypeOf<FatFilesystemDriverAdapter>());
  }

  [Test, Category("Driver")]
  public void NativeFatSession_ReadsArbitraryOffsetsWithoutExtractingWholeFile() {
    var payload = Enumerable.Range(0, 1700).Select(i => (byte)(i * 29 + 7)).ToArray();
    var writer = new FatWriter();
    writer.SetVolumeSerial(0x11223344);
    writer.AddFile("HELLO.BIN", payload);
    using var image = new MemoryStream(writer.Build(), writable: false);

    var profile = FormatRegistry.ProbeFilesystem("Fat", image);
    Assert.That(profile.CanMount, Is.True);
    Assert.That(profile.CanMountWritable, Is.True);
    Assert.That(profile.ProfileName, Does.StartWith("FAT"));

    using var session = FormatRegistry.OpenFilesystem(
      "Fat", image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var node = session.Lookup(session.RootNodeId, "hello.bin");
    Assert.That(node, Is.Not.Null, "FAT lookup should be case-insensitive when unambiguous.");

    using var handle = session.OpenFile(node!.Value, FileAccess.Read);
    var buffer = new byte[777];
    Assert.That(handle.Read(503, buffer), Is.EqualTo(buffer.Length));
    Assert.That(buffer, Is.EqualTo(payload.AsSpan(503, buffer.Length).ToArray()));

    var tail = new byte[64];
    Assert.That(handle.Read(payload.Length - 17, tail), Is.EqualTo(17));
    Assert.That(tail.AsSpan(0, 17).ToArray(), Is.EqualTo(payload[^17..]));
    Assert.That(handle.Read(payload.Length, tail), Is.Zero);
  }

  [Test, Category("Driver")]
  public void FatDriver_ComposesWithRandomAccessBlockDevice() {
    var payload = Enumerable.Range(0, 900).Select(i => (byte)(255 - i)).ToArray();
    var writer = new FatWriter();
    writer.SetVolumeSerial(0x55667788);
    writer.AddFile("BLOCK.DAT", payload);
    using var image = new MemoryStream(writer.Build(), writable: false);
    using var device = new StreamBlockDevice(image, 512, writable: false, leaveOpen: true);
    var adapter = new FatFilesystemDriverAdapter();

    using var session = adapter.OpenFilesystem(
      device, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var node = session.Lookup(session.RootNodeId, "BLOCK.DAT");
    Assert.That(node, Is.Not.Null);
    using var handle = session.OpenFile(node!.Value, FileAccess.Read);
    var bytes = new byte[300];
    Assert.That(handle.Read(510, bytes), Is.EqualTo(bytes.Length));
    Assert.That(bytes, Is.EqualTo(payload.AsSpan(510, bytes.Length).ToArray()));
  }

  [Test, Category("Driver")]
  public void WritableSession_PreservesOpenHandleAcrossRenameAndPersistsGrowth() {
    var payload = Enumerable.Range(0, 1700).Select(i => (byte)(i * 17 + 3)).ToArray();
    var writer = new FatWriter();
    writer.SetVolumeSerial(0x10203040);
    writer.AddFile("HELLO.BIN", payload);
    using var image = new MemoryStream(writer.Build(), writable: true);
    var adapter = new FatFilesystemDriverAdapter();

    FilesystemNodeId node;
    using (var session = adapter.OpenFilesystem(
      image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      node = session.Lookup(session.RootNodeId, "HELLO.BIN")!.Value;
      using var handle = session.OpenFile(node, FileAccess.ReadWrite);
      handle.Write(503, "renamed"u8);

      session.Rename(session.RootNodeId, "HELLO.BIN", session.RootNodeId, "renamed-long-name.bin", replace: false);
      Assert.That(session.Lookup(session.RootNodeId, "HELLO.BIN"), Is.Null);
      Assert.That(session.Lookup(session.RootNodeId, "renamed-long-name.bin"), Is.EqualTo(node));
      Assert.That(handle.NodeId, Is.EqualTo(node));

      handle.SetLength(2500);
      handle.Write(2300, "tail-after-growth"u8);
      handle.Flush();
      Assert.That(handle.Length, Is.EqualTo(2500));
    }

    image.Position = 0;
    using var reader = new FatReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(e => e.Name.Equals("renamed-long-name.bin", StringComparison.OrdinalIgnoreCase));
    var bytes = reader.Extract(entry);
    Assert.That(bytes, Has.Length.EqualTo(2500));
    Assert.That(bytes.AsSpan(503, 7).ToArray(), Is.EqualTo("renamed"u8.ToArray()));
    Assert.That(bytes.AsSpan(1700, 600).ToArray(), Is.All.Zero, "growth holes must be zero-initialized before publication");
    Assert.That(bytes.AsSpan(2300, 17).ToArray(), Is.EqualTo("tail-after-growth"u8.ToArray()));
  }

  [Test, Category("Driver")]
  public void WritableSession_CreatesNestedDirectoriesAndFiles() {
    var writer = new FatWriter();
    writer.SetVolumeSerial(0x20304050);
    using var image = new MemoryStream(writer.Build(), writable: true);
    var adapter = new FatFilesystemDriverAdapter();

    using (var session = adapter.OpenFilesystem(
      image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var docs = session.CreateDirectory(session.RootNodeId, "Documents");
      var nested = session.CreateDirectory(docs, "Nested");
      var file = session.CreateFile(nested, "mount-grade.txt");
      using var handle = session.OpenFile(file, FileAccess.ReadWrite);
      handle.Write(0, "bounded FAT write"u8);
      session.SetMetadata(file, new FilesystemMetadataPatch(NativeAttributes: 0x20));
      session.Flush();
    }

    image.Position = 0;
    using var reader = new FatReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(e => e.Name.Equals("Documents/Nested/mount-grade.txt", StringComparison.OrdinalIgnoreCase));
    Assert.That(reader.Extract(entry), Is.EqualTo("bounded FAT write"u8.ToArray()));
  }

  [Test, Category("Driver")]
  public void WritableSession_UnlinkKeepsOpenHandleAliveUntilClose() {
    var payload = Enumerable.Range(0, 900).Select(i => (byte)(i ^ 0x5A)).ToArray();
    var writer = new FatWriter();
    writer.SetVolumeSerial(0x30405060);
    writer.AddFile("OPEN.BIN", payload);
    using var image = new MemoryStream(writer.Build(), writable: true);
    var adapter = new FatFilesystemDriverAdapter();

    using (var session = adapter.OpenFilesystem(
      image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var node = session.Lookup(session.RootNodeId, "OPEN.BIN")!.Value;
      using var handle = session.OpenFile(node, FileAccess.Read);
      session.DeleteFile(session.RootNodeId, "OPEN.BIN");
      Assert.That(session.Lookup(session.RootNodeId, "OPEN.BIN"), Is.Null);

      var bytes = new byte[payload.Length];
      Assert.That(handle.Read(0, bytes), Is.EqualTo(payload.Length));
      Assert.That(bytes, Is.EqualTo(payload));
    }

    image.Position = 0;
    using var reader = new FatReader(image, leaveOpen: true);
    Assert.That(reader.Entries.Any(e => e.Name.Equals("OPEN.BIN", StringComparison.OrdinalIgnoreCase)), Is.False);
  }

  [Test, Category("Driver"), Category("Corruption")]
  public void WritableReadiness_FailsClosedWhenFatCopiesDisagree() {
    var writer = new FatWriter();
    writer.SetVolumeSerial(0x99AABBCC);
    writer.AddFile("A.BIN", Enumerable.Repeat((byte)0xA5, 700).ToArray());
    var bytes = writer.Build();

    // Default 1.44 MB FAT12 geometry: reserved sector 1, each FAT is 9 sectors.
    // Cluster 2's packed FAT12 entry starts at byte offset cluster + cluster/2.
    var secondFatStart = (1 + 9) * 512;
    bytes[secondFatStart + 3] ^= 0x01;
    using var image = new MemoryStream(bytes, writable: false);

    var profile = new FatFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.False);
    Assert.That(profile.Limitations.Any(text => text.Contains("copies disagree", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  [Test, Category("Driver"), Category("Corruption")]
  public void WritableReadiness_RejectsDirtyFat16ButKeepsReadOnlyMount() {
    var writer = new FatWriter();
    writer.SetVolumeSerial(0x40506070);
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    var bytes = writer.Build(totalSectors: 8192, forcedFatType: 16);

    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(11, 2));
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2));
    var fatCount = bytes[16];
    var fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
    for (var copy = 0; copy < fatCount; ++copy) {
      var offset = checked((reservedSectors + copy * fatSectors) * bytesPerSector + 2);
      var entry = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
      BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)(entry & ~0x8000));
    }

    using var image = new MemoryStream(bytes, writable: true);
    var profile = new FatFilesystemDriverAdapter().ProbeFilesystem(image);
    Assert.That(profile.CanMount, Is.True);
    Assert.That(profile.CanMountWritable, Is.False);
    Assert.That(profile.Limitations.Any(text => text.Contains("dirty", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  [Test, Category("Driver"), Category("Contract")]
  public void FatReadiness_PromotesCleanOrdinaryFatToMountedWriteCompleteness() {
    var writer = new FatWriter();
    writer.SetVolumeSerial(0xCAFEBABE);
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    using var image = new MemoryStream(writer.Build(), writable: true);

    var report = FormatRegistry.AssessFilesystemDriver("Fat", image, FilesystemDriverTarget.ReadWrite);
    Assert.That(report.UsesNativeProvider, Is.True);
    Assert.That(report.Derivable, Is.True);
    Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.AllocationMap), Is.True);
    Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.WriteData), Is.True);
    Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.NamespaceMutation), Is.True);
    Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.DurabilityModel), Is.True);
    Assert.That(report.Blockers, Is.Empty);
  }
}
