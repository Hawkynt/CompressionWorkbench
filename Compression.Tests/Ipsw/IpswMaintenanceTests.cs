using System.IO.Compression;
using Compression.Registry;
using FileFormat.Ipsw;

namespace Compression.Tests.Ipsw;

[TestFixture]
public class IpswMaintenanceTests {

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesFeasibleMaintenanceContracts() {
    var descriptor = new IpswFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
    });
  }

  [Test, Category("RoundTrip")]
  public void Defragment_RemovesDeadZipSpaceAndPreservesLiveMembers() {
    var descriptor = new IpswFormatDescriptor();
    using var image = BuildIpsw(
      ("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ("Firmware/dead.bin", Enumerable.Repeat((byte)0xA5, 64 * 1024).ToArray()),
      ("Firmware/dfu/iBSS.test.RELEASE.im4p", [0xDE, 0xAD, 0xBE, 0xEF]));

    ((IArchiveModifiable)descriptor).Remove(image, ["Firmware/dead.bin"]);
    var beforeLength = image.Length;

    ((IArchiveDefragmentable)descriptor).Defragment(image);

    Assert.That(image.Length, Is.LessThan(beforeLength));
    Assert.That(ReadEntry(image, "Firmware/dfu/iBSS.test.RELEASE.im4p"), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
    Assert.That(RawNames(image), Does.Not.Contain("Firmware/dead.bin"));
    Assert.That(RawNames(image), Does.Contain("Firmware/dfu/iBSS.test.RELEASE.im4p"));
  }

  [Test, Category("RoundTrip")]
  public void Shrink_TightPacksDeadZipSpaceAndPreservesLiveMembers() {
    var descriptor = new IpswFormatDescriptor();
    using var image = BuildIpsw(
      ("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ("Firmware/dead.bin", Enumerable.Repeat((byte)0x5A, 64 * 1024).ToArray()),
      ("Restore.plist", "restore"u8.ToArray()));
    ((IArchiveModifiable)descriptor).Remove(image, ["Firmware/dead.bin"]);

    using var output = new MemoryStream();
    ((IArchiveShrinkable)descriptor).Shrink(image, output);

    Assert.That(output.Length, Is.LessThan(image.Length));
    Assert.That(ReadEntry(output, "Restore.plist"), Is.EqualTo("restore"u8.ToArray()));
    Assert.That(RawNames(output), Is.EquivalentTo(new[] { "BuildManifest.plist", "Restore.plist" }));
  }

  [Test, Category("RoundTrip")]
  public void Purge_LeavesAValidEmptyIpswContainer() {
    var descriptor = new IpswFormatDescriptor();
    using var image = BuildIpsw(
      ("BuildManifest.plist", "<plist/>"u8.ToArray()),
      ("Firmware/dfu/iBSS.test.RELEASE.im4p", [1, 2, 3, 4]));

    ((IArchivePurgeable)descriptor).Purge(image);

    Assert.That(RawNames(image), Is.Empty);
    image.Position = 0;
    var listed = descriptor.List(image, null).Select(entry => entry.Name).ToArray();
    Assert.That(listed, Is.EquivalentTo(new[] { "FULL.ipsw", "metadata.ini" }));
  }

  [Test, Category("RoundTrip")]
  public void Wipe_PreservesZipDataDescriptors() {
    var original = BuildDataDescriptorIpsw();
    using var image = new MemoryStream(original.ToArray());
    var descriptor = new IpswFormatDescriptor();

    var layout = ((IArchiveLayoutMap)descriptor).EnumerateLayout(image).ToArray();
    Assert.That(layout, Has.Some.Matches<DefragBlockInfo>(block =>
      block.Kind == DefragBlockKind.MetadataReserved &&
      block.FileName?.StartsWith("Data descriptor:", StringComparison.Ordinal) == true));

    image.Position = 0;
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);

    Assert.Multiple(() => {
      Assert.That(wiped, Is.Zero);
      Assert.That(image.ToArray(), Is.EqualTo(original));
    });
  }

  [Test, Category("RoundTrip")]
  public void Create_FileBackedInputStreamsPayloadAndPreservesNestedPath() {
    var temp = Path.GetTempFileName();
    try {
      var payload = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i * 31)).ToArray();
      File.WriteAllBytes(temp, payload);
      var descriptor = new IpswFormatDescriptor();
      using var image = new MemoryStream();

      ((IArchiveCreatable)descriptor).Create(image, [
        ArchiveInputInfo.FromFile(new FileInfo(temp), "Firmware/dfu/streamed.bin"),
      ], new FormatCreateOptions());

      Assert.That(ReadEntry(image, "Firmware/dfu/streamed.bin"), Is.EqualTo(payload));
      image.Position = 0;
      Assert.That(descriptor.List(image, null).Select(entry => entry.Name), Does.Contain("Firmware/dfu/streamed.bin"));
    } finally {
      File.Delete(temp);
    }
  }

  private static MemoryStream BuildIpsw(params (string Name, byte[] Data)[] entries) {
    var image = new MemoryStream();
    using (var zip = new ZipArchive(image, ZipArchiveMode.Create, leaveOpen: true)) {
      foreach (var (name, data) in entries) {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(data);
      }
    }
    image.Position = 0;
    return image;
  }

  private static byte[] BuildDataDescriptorIpsw() {
    using var backing = new MemoryStream();
    using (var forwardOnly = new ForwardOnlyWriteStream(backing))
    using (var zip = new ZipArchive(forwardOnly, ZipArchiveMode.Create, leaveOpen: true)) {
      var entry = zip.CreateEntry("BuildManifest.plist", CompressionLevel.Optimal);
      using var stream = entry.Open();
      stream.Write(Enumerable.Repeat((byte)'A', 4096).ToArray());
    }
    return backing.ToArray();
  }

  private static string[] RawNames(Stream image) {
    image.Position = 0;
    using var zip = new ZipArchive(image, ZipArchiveMode.Read, leaveOpen: true);
    return zip.Entries.Select(entry => entry.FullName).ToArray();
  }

  private static byte[] ReadEntry(Stream image, string name) {
    image.Position = 0;
    using var zip = new ZipArchive(image, ZipArchiveMode.Read, leaveOpen: true);
    var entry = zip.GetEntry(name) ?? throw new KeyNotFoundException(name);
    using var source = entry.Open();
    using var output = new MemoryStream();
    source.CopyTo(output);
    return output.ToArray();
  }

  private sealed class ForwardOnlyWriteStream(Stream inner) : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
      => inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing) {
      if (disposing)
        inner.Flush();
      base.Dispose(disposing);
    }
  }
}
