using System.IO.Compression;
using Compression.Registry;
using FileFormat.Aff4;

namespace Compression.Tests.Aff4;

/// <summary>
/// A volume past 4 GiB: members whose local headers start beyond the 32-bit range must be found
/// through ZIP64 central-directory offsets and the ZIP64 end records. Writes about 5 GiB, so it is
/// in the advisory Performance tier and skips when the temp volume lacks room.
/// </summary>
[TestFixture]
[Category("Performance")]
public sealed class Aff4LargeVolumeTests {
  [Test]
  public void GivenMembersBeyond4GiB_WhenRead_ThenZip64OffsetsLocateEveryMember() {
    const long fileSize = 1L << 30;
    const int copies = 5;
    var temp = Path.GetTempPath();
    var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(temp))!).AvailableFreeSpace;
    if (free < (copies + 2) * fileSize) Assert.Ignore($"needs {(copies + 2)} GiB free in {temp}, has {free >> 30} GiB");

    var dir = Path.Combine(temp, "cwb_aff4big_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(dir);
    try {
      var source = Path.Combine(dir, "gib.bin");
      using (var fs = File.Create(source)) {
        var block = new byte[1 << 20];
        for (var i = 0; i < fileSize / block.Length; ++i) {
          block[0] = (byte)i;
          fs.Write(block);
        }
      }
      var inputs = Enumerable.Range(0, copies).Select(i => new ArchiveInputInfo(source, $"copy{i}.bin", false)).ToList();
      var volume = Path.Combine(dir, "big.aff4");
      using (var fs = File.Create(volume))
        new Aff4FormatDescriptor().Create(fs, inputs, new FormatCreateOptions("stored"));
      Assert.That(new FileInfo(volume).Length, Is.GreaterThan(4L << 30));

      using (var zip = ZipFile.OpenRead(volume)) {
        var last = zip.Entries.Last(e => e.FullName.StartsWith("aff4://", StringComparison.Ordinal));
        Assert.That(last.Length, Is.EqualTo(fileSize));
        using var stream = last.Open();
        var head = new byte[1 << 20];
        stream.ReadExactly(head);
        Assert.That(head[0], Is.EqualTo(0), "first block of the last copy");
      }

      using var volumeStream = File.OpenRead(volume);
      var listed = new Aff4FormatDescriptor().List(volumeStream, null).Where(e => e.Kind == "file").ToList();
      Assert.That(listed.Select(e => e.Name), Is.EquivalentTo(inputs.Select(i => i.ArchiveName)));
      Assert.That(listed, Has.All.Matches<ArchiveEntryInfo>(e => e.OriginalSize == fileSize));
      var outDir = Path.Combine(dir, "out");
      new Aff4FormatDescriptor().Extract(volumeStream, outDir, null, [$"copy{copies - 1}.bin"]);
      var extracted = Path.Combine(outDir, $"copy{copies - 1}.bin");
      Assert.That(new FileInfo(extracted).Length, Is.EqualTo(fileSize));
      using (var a = File.OpenRead(extracted))
      using (var b = File.OpenRead(source))
        Assert.That(System.Security.Cryptography.SHA256.HashData(a), Is.EqualTo(System.Security.Cryptography.SHA256.HashData(b)));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
