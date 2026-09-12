using Compression.Lib;
using Compression.Registry;
using FileSystem.D71;
using FileSystem.D81;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class CbmDosD71D81FilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [TestCase("D71")]
  [TestCase("D81")]
  public void RegistryUsesNativeWritableSidecar(string formatId) {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage(formatId);
    Assert.Multiple(() => {
      Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
      Assert.That(coverage.HasNativeReadinessProvider, Is.True);
    });
  }

  [Test]
  public void D71WritableSession_PreservesNodeIdentityAndPersistsNamespaceAndData() {
    var original = Enumerable.Range(0, 700).Select(i => (byte)(i * 17)).ToArray();
    var writer = new D71Writer();
    writer.AddFile("HELLO", original);
    using var image = new MemoryStream(writer.Build("DRIVER", "71"), writable: true);

    AssertMountedLifecycle("D71", image, original);
  }

  [Test]
  public void D81WritableSession_PreservesNodeIdentityAndPersistsNamespaceAndData() {
    var original = Enumerable.Range(0, 700).Select(i => (byte)(i * 29)).ToArray();
    var writer = new D81Writer();
    writer.AddFile("HELLO", original);
    using var image = new MemoryStream(writer.Build("DRIVER", "81"), writable: true);

    AssertMountedLifecycle("D81", image, original);
  }

  [Test]
  public void D71Writer_EmitsNativeReservedBamTrackAndInterleave() {
    var writer = new D71Writer();
    writer.AddFile("CHAIN", Enumerable.Range(0, 600).Select(i => (byte)i).ToArray());
    var image = writer.Build("D71", "71");

    const int track18Offset = 17 * 21 * 256;
    const int track53IndexOnSide2 = 53 - 36;
    Assert.That(image[track18Offset + 0xDD + track53IndexOnSide2], Is.Zero,
      "1571 DOS reserves the whole side-two BAM track");

    var side2BamOffset = SectorOffsetD71(53, 0);
    var track53BitmapOffset = side2BamOffset + track53IndexOnSide2 * 3;
    Assert.That(image.AsSpan(track53BitmapOffset, 3).ContainsAnyExcept((byte)0), Is.False);

    var first = SectorOffsetD71(1, 6);
    Assert.Multiple(() => {
      Assert.That(image[first], Is.EqualTo(1));
      Assert.That(image[first + 1], Is.EqualTo(12), "native 1571 sequential-file interleave is six sectors");
    });
  }

  [Test]
  public void D81Writer_EmitsCanonicalBamLinksIdsAndDirectoryInterleave() {
    var writer = new D81Writer();
    for (var i = 0; i < 9; ++i)
      writer.AddFile($"F{i}", [(byte)i]);
    var image = writer.Build("D81", "XY");

    var header = SectorOffsetD81(40, 0);
    var bam1 = SectorOffsetD81(40, 1);
    var bam2 = SectorOffsetD81(40, 2);
    var dir = SectorOffsetD81(40, 3);

    Assert.Multiple(() => {
      Assert.That(image[bam1], Is.EqualTo(40));
      Assert.That(image[bam1 + 1], Is.EqualTo(2));
      Assert.That(image[bam2], Is.Zero);
      Assert.That(image[bam2 + 1], Is.EqualTo(0xFF));
      Assert.That(image[bam1 + 4], Is.EqualTo(image[header + 0x16]));
      Assert.That(image[bam1 + 5], Is.EqualTo(image[header + 0x17]));
      Assert.That(image[bam2 + 4], Is.EqualTo((byte)'X'));
      Assert.That(image[bam2 + 5], Is.EqualTo((byte)'Y'));
      Assert.That(image[dir], Is.EqualTo(40));
      Assert.That(image[dir + 1], Is.EqualTo(4), "1581 directory sectors use interleave one");
    });
  }

  [TestCase("D71")]
  [TestCase("D81")]
  public void Probe_RefusesWritableMountWhenBamOwnershipIsInconsistent(string formatId) {
    var bytes = formatId == "D71" ? BuildD71() : BuildD81();
    if (formatId == "D71") {
      var bam = SectorOffsetD71(18, 0);
      // Track 1 sector 0 is free in the native-interleave image. Claim it in
      // the BAM without adding a namespace owner, while keeping the count in sync.
      bytes[bam + 4]--;
      bytes[bam + 5] &= 0xFE;
    } else {
      var bam = SectorOffsetD81(40, 1);
      // Same corruption for D81 track 1 sector 0.
      bytes[bam + 16]--;
      bytes[bam + 17] &= 0xFE;
    }

    using var image = new MemoryStream(bytes, writable: true);
    var profile = FormatRegistry.ProbeFilesystem(formatId, image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Limitations.Any(x => x.Contains("BAM", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }

  [TestCase("D71")]
  [TestCase("D81")]
  public void BlockDevice_PreservesTrailingErrorTable(string formatId) {
    var dataLength = formatId == "D71" ? D71BlockDevice.DataLength : D81BlockDevice.DataLength;
    var sectorCount = formatId == "D71" ? D71BlockDevice.SectorCount : D81BlockDevice.SectorCount;
    var payload = formatId == "D71" ? BuildD71() : BuildD81();
    Array.Resize(ref payload, dataLength + sectorCount);
    payload.AsSpan(dataLength, sectorCount).Fill(0xA5);
    using var stream = new MemoryStream(payload, writable: true);

    using (IRandomAccessBlockDevice device = formatId == "D71"
      ? new D71BlockDevice(stream, writable: true, leaveOpen: true)
      : new D81BlockDevice(stream, writable: true, leaveOpen: true)) {
      var sector = new byte[256];
      sector.AsSpan().Fill(0x5A);
      device.WriteBlocks(0, sector);
      device.Flush();
    }

    Assert.That(stream.ToArray().AsSpan(dataLength, sectorCount).ToArray(),
      Is.EqualTo(Enumerable.Repeat((byte)0xA5, sectorCount).ToArray()).AsCollection);
  }

  private static void AssertMountedLifecycle(string formatId, MemoryStream image, byte[] original) {
    image.Position = 0;
    var profile = FormatRegistry.ProbeFilesystem(formatId, image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.True);
      Assert.That(profile.CanMountWritable, Is.True, string.Join("; ", profile.Limitations));
      Assert.That(profile.MutationModel, Is.EqualTo(FilesystemMutationModel.Direct));
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.RandomAccess), Is.True);
      Assert.That(profile.Capabilities.HasFlag(FilesystemDriverCapabilities.Transactions), Is.False);
    });

    FilesystemNodeId createdId;
    image.Position = 0;
    using (var fs = FormatRegistry.OpenFilesystem(
      formatId, image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var root = fs.RootNodeId;
      var helloId = fs.Lookup(root, "hello");
      Assert.That(helloId.HasValue, Is.True);
      using (var hello = fs.OpenFile(helloId!.Value, FileAccess.Read)) {
        var slice = new byte[97];
        Assert.That(hello.Read(123, slice), Is.EqualTo(slice.Length));
        Assert.That(slice, Is.EqualTo(original.AsSpan(123, slice.Length).ToArray()).AsCollection);
      }

      createdId = fs.CreateFile(root, "newfile");
      using (var handleA = fs.OpenFile(createdId, FileAccess.ReadWrite))
      using (var handleB = fs.OpenFile(createdId, FileAccess.ReadWrite)) {
        handleA.Write(0, "0123456789"u8);
        var observed = new byte[4];
        Assert.That(handleB.Read(3, observed), Is.EqualTo(4));
        Assert.That(observed, Is.EqualTo("3456"u8.ToArray()).AsCollection);
        handleB.Write(4, "ABCD"u8);
        handleA.SetLength(12);
        handleA.Flush();
      }

      fs.Rename(root, "NEWFILE", root, "RENAMED", replace: false);
      Assert.That(fs.Lookup(root, "RENAMED"), Is.EqualTo(createdId), "rename must not change node identity");
      fs.DeleteFile(root, "HELLO");
      fs.Flush();
    }

    image.Position = 0;
    using var reopened = FormatRegistry.OpenFilesystem(
      formatId, image, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var reopenedRoot = reopened.RootNodeId;
    Assert.Multiple(() => {
      Assert.That(reopened.Lookup(reopenedRoot, "HELLO"), Is.Null);
      Assert.That(reopened.Lookup(reopenedRoot, "RENAMED").HasValue, Is.True);
    });
    var renamedId = reopened.Lookup(reopenedRoot, "RENAMED")!.Value;
    using var renamed = reopened.OpenFile(renamedId, FileAccess.Read);
    var payload = new byte[12];
    Assert.That(renamed.Read(0, payload), Is.EqualTo(payload.Length));
    Assert.That(payload, Is.EqualTo(new byte[] {
      (byte)'0', (byte)'1', (byte)'2', (byte)'3',
      (byte)'A', (byte)'B', (byte)'C', (byte)'D',
      (byte)'8', (byte)'9', 0, 0,
    }).AsCollection);
  }

  private static byte[] BuildD71() {
    var writer = new D71Writer();
    writer.AddFile("HELLO", [1, 2, 3]);
    return writer.Build("TEST", "71");
  }

  private static byte[] BuildD81() {
    var writer = new D81Writer();
    writer.AddFile("HELLO", [1, 2, 3]);
    return writer.Build("TEST", "81");
  }

  private static int SectorOffsetD71(int track, int sector) {
    var sectors = 0;
    for (var t = 1; t < track; ++t)
      sectors += t <= 17 || t is >= 36 and <= 52 ? 21
        : t <= 24 || t is >= 53 and <= 59 ? 19
        : t <= 30 || t is >= 60 and <= 65 ? 18
        : 17;
    return (sectors + sector) * 256;
  }

  private static int SectorOffsetD81(int track, int sector)
    => ((track - 1) * 40 + sector) * 256;
}
