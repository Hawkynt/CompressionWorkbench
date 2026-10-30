#pragma warning disable CS1591
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

/// <summary>
/// ProDOS volumes stored in DOS 3.3 sector order — the usual container of Apple III SOS
/// disks, whose file system is ProDOS's. The reference disk was written by CiderPress II;
/// provenance in <c>ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public sealed class ProDosDosOrderTests {

  private static byte[] Disk() {
    using var resource = typeof(ProDosDosOrderTests).Assembly.GetManifestResourceStream("ProDosVectors.cp2-prodos.do.gz")
      ?? throw new FileNotFoundException("embedded cp2-prodos.do.gz is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gzip.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, string> Manifest() {
    using var resource = typeof(ProDosDosOrderTests).Assembly.GetManifestResourceStream("ProDosVectors.manifest.txt")!;
    using var text = new StreamReader(resource);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    while (text.ReadLine() is { } line)
      if (line.Length > 65) result[line[65..]] = line[..64];
    return result;
  }

  [Test, Category("ThirdParty")]
  public void GivenADosOrderDiskCiderPressWrote_WhenRead_ThenEveryFileMatchesItsSource() {
    using var r = new ProDosReader(Disk());
    Assert.That(r.IsDosOrder, Is.True);
    Assert.That(r.VolumeName, Is.EqualTo("NEWDISK"));
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.FullPath, StringComparer.Ordinal);
    var manifest = Manifest();
    Assert.That(files.Keys, Is.EquivalentTo(manifest.Keys));
    foreach (var (path, sha) in manifest)
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.Extract(files[path]))), Is.EqualTo(sha), path);
    Assert.That(r.Entries.Single(e => e.FullPath == "SUBDIR").IsDirectory, Is.True);
  }

  [Test]
  public void GivenTheSameDiskInBlockOrder_WhenRead_ThenItIsNotTakenForDosOrder() {
    var blockOrder = ProDosReader.DosOrderToProDosOrder(Disk());
    using var r = new ProDosReader(blockOrder);
    Assert.That(r.IsDosOrder, Is.False);
    Assert.That(r.Entries.Count(e => !e.IsDirectory), Is.EqualTo(4));
  }

  [Test]
  public void GivenADosOrderDisk_WhenEdited_ThenEveryEditIsRefusedAndTheImageIsUntouched() {
    var before = Disk();
    using var image = new MemoryStream();
    image.Write(before);
    var d = new ProDosFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => d.Add(image, [ArchiveInputInfo.InMemory("NEW", new byte[] { 1 })]));
    Assert.Throws<NotSupportedException>(() => d.Remove(image, ["HELLO.TXT"]));
    Assert.Throws<NotSupportedException>(() => d.WipeUnusedSpace(image));
    Assert.Throws<NotSupportedException>(() => d.Defragment(image));
    Assert.Throws<NotSupportedException>(() => d.MoveExtent(image, 0, 512, 512));
    Assert.That(d.EnumerateExtents(image), Is.Empty);
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenAnImageOfAnotherLength_WhenReordered_ThenItIsRejected()
    => Assert.Throws<ArgumentException>(() => ProDosReader.DosOrderToProDosOrder(new byte[143_359]));

  [Test]
  public void GivenA140KImageWithNoVolumeHeaderInEitherOrder_WhenRead_ThenItIsRejected()
    => Assert.Throws<InvalidDataException>(() => _ = new ProDosReader(new byte[ProDosReader.FloppyImageLength]));

  /// <summary>
  /// Real Apple III SOS disks from apple3.org (Apple software, not checked in). Set
  /// <c>CWB_SOS_DISK</c> to <c>Apple3SystemUtilData.dsk</c>; the expected listing is what
  /// CiderPress II's <c>cp2 catalog</c> reported for it.
  /// </summary>
  [Test, Category("ThirdParty")]
  public void GivenTheAppleIiiSystemUtilitiesDataDisk_WhenRead_ThenTheListingMatchesCiderPress() {
    var path = Environment.GetEnvironmentVariable("CWB_SOS_DISK");
    if (path is null || !File.Exists(path)) Assert.Ignore("set CWB_SOS_DISK to Apple3SystemUtilData.dsk from apple3.org");
    using var r = new ProDosReader(File.ReadAllBytes(path));
    Assert.That(r.IsDosOrder, Is.True);
    Assert.That(r.VolumeName, Is.EqualTo("UTILITIES.DATA"));
    var expected = new Dictionary<string, long> {
      ["CONSOLE.DRIVER"] = 6144, ["RS232.DRIVER"] = 3584, ["QUME.DRIVER"] = 2048, ["AUDIO.DRIVER"] = 2048,
      ["GRAFIX.DRIVER"] = 6656, ["SILENTYP.DRIVER"] = 6144, ["FMTDX.DRIVER"] = 2560,
      ["KEYBOARD.LAYOUT/SHOLES"] = 1536, ["KEYBOARD.LAYOUT/DVORAK"] = 1536,
      ["FONTS/STANDARD"] = 1024, ["FONTS/APPLE"] = 1024, ["FONTS/ROMAN"] = 1024, ["FONTS/BYTE"] = 1024,
    };
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.FullPath, e => e.Size);
    foreach (var (name, size) in expected) {
      Assert.That(files.TryGetValue(name, out var actual), Is.True, name);
      Assert.That(actual, Is.EqualTo(size), name);
    }
  }
}
