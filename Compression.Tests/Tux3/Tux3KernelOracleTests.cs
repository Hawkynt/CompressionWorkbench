using System.Security.Cryptography;
using System.Text;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

/// <summary>
/// The linux-tux3 kernel judges what the writer makes, in both directions: it mounts each volume,
/// resolves every path and reads every byte, and its manifest must be what was written; then it
/// writes into a volume the writer made, and the reader must see exactly what the kernel reads back.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class Tux3KernelOracleTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), "cwb_tux3kernel_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

  [TestCase(0x7FFF), TestCase(1), Category("HappyPath")]
  public void GivenEveryObjectKindTheWriterMakes_WhenTheKernelMountsTheVolume_ThenItReadsBackExactlyWhatWasWritten(int maxDirectBlocks) {
    Tux3KernelOracle.Require();
    var (writer, expected) = Tux3WriterTests.EveryKind(maxDirectBlocks);
    var path = Path.Combine(this._tmpDir, $"kinds-{maxDirectBlocks}.img");
    File.WriteAllBytes(path, writer.Build());
    var mount = Tux3KernelOracle.Read(path);
    Assert.That(mount.Complaints, Is.Empty, "the kernel complained:\n" + string.Join('\n', mount.Complaints));
    Assert.That(mount.Manifest.Keys.Where(static k => k != ".").Order(StringComparer.Ordinal), Is.EqualTo(expected.Keys.Order(StringComparer.Ordinal)));
    Assert.Multiple(() => {
      foreach (var (name, (type, data, links)) in expected) {
        var got = mount.Manifest[name];
        switch (type) {
          case "file":
            Assert.That((got.Size, got.Value, got.Links), Is.EqualTo(((long)data.Length, Md5(data), links)), name);
            break;
          case "symlink":
            Assert.That((got.Type, got.Value), Is.EqualTo(("symbolic link", Encoding.ASCII.GetString(data))), name);
            break;
          case "dir":
            Assert.That(got.Type, Is.EqualTo("directory"), name);
            break;
          case "fifo":
            Assert.That(got.Type, Is.EqualTo("fifo"), name);
            break;
        }
      }
      Assert.That((mount.Manifest["special/char"].Type, mount.Manifest["special/char"].Device), Is.EqualTo(("character special file", 1UL << 20 | 3)));
      Assert.That((mount.Manifest["special/block"].Type, mount.Manifest["special/block"].Device), Is.EqualTo(("block special file", 259UL << 20 | 300_000)));
      Assert.That(mount.Manifest["empty"].Links, Is.EqualTo(3u));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenOwnerModeAndTime_WhenTheKernelMountsTheVolume_ThenItSeesThem() {
    Tux3KernelOracle.Require();
    var writer = new Tux3Writer();
    writer.AddFile("owned", [1, 2, 3], new Tux3Writer.Metadata(0x1E9, 1234, 5678, 981_173_106_000_000_000));
    var path = Path.Combine(this._tmpDir, "owned.img");
    File.WriteAllBytes(path, writer.Build());
    var got = Tux3KernelOracle.Read(path).Manifest["owned"];
    Assert.That((got.Mode, got.Uid, got.Gid, got.ModifiedTime), Is.EqualTo((0x1E9, 1234u, 5678u, 981_173_106L)));
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeTheWriterMade_WhenTheKernelWritesIntoIt_ThenTheReaderSeesWhatTheKernelReadsBack() {
    Tux3KernelOracle.Require();
    var (writer, _) = Tux3WriterTests.EveryKind();
    var path = Path.Combine(this._tmpDir, "kernel-writes.img");
    File.WriteAllBytes(path, writer.Build());
    var prelude = string.Join('\n',
      "mount -t tux3 /dev/vda /mnt || { echo MOUNTFAIL; exit 1; }",
      "mkdir /mnt/kernel",
      "i=0; while [ $i -lt 200 ]; do echo \"kernel $i\" > /mnt/kernel/k$i; i=$((i+1)); done",
      "dd if=/dev/urandom of=/mnt/kernel/big bs=4096 count=40 2>/dev/null",
      "echo appended >> /mnt/sizes/f100",
      "ln -s ../sizes/f1 /mnt/kernel/link",
      "ln /mnt/sizes/f4096 /mnt/kernel/second-name",
      "rm /mnt/many/m007 /mnt/links/hard",
      "mv /mnt/names/x /mnt/kernel/moved",
      "umount /mnt || echo UMOUNTFAIL");
    var mount = Tux3KernelOracle.Read(path, prelude);
    Assert.That(mount.Complaints, Is.Empty, "the kernel complained:\n" + string.Join('\n', mount.Complaints));
    Assert.That(mount.Manifest.ContainsKey("kernel/k199") && mount.Manifest.ContainsKey("kernel/moved") && !mount.Manifest.ContainsKey("many/m007"),
      Is.True, "the kernel did what it was asked:\n" + mount.Log);

    using var reader = new Tux3Reader(new MemoryStream(File.ReadAllBytes(path)));
    Assert.Multiple(() => Tux3WriterTests.AssertMatches(reader, mount.Manifest, "kernel-writes"));
  }
}
