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
    Assert.That(profile.CanMountWritable, Is.False);
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

  [Test, Category("Driver"), Category("Contract")]
  public void FatReadiness_SeparatesExistingOfflineMutationFromMountedWriteCompleteness() {
    var writer = new FatWriter();
    writer.SetVolumeSerial(0xCAFEBABE);
    writer.AddFile("A.TXT", "abc"u8.ToArray());
    using var image = new MemoryStream(writer.Build(), writable: true);

    var report = FormatRegistry.AssessFilesystemDriver("Fat", image, FilesystemDriverTarget.ReadWrite);
    Assert.That(report.UsesNativeProvider, Is.True);
    Assert.That(report.Derivable, Is.False);
    Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.AllocationMap), Is.True);
    Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.WriteData), Is.False);
    Assert.That(report.Blockers.Any(text => text.Contains("FatModifier", StringComparison.Ordinal)), Is.True);
  }
}
