using System.Text;
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;
using FileSystem.Stacker;

namespace Compression.Tests.Cvf;

/// <summary>
/// Whatever the four CVF descriptors' <c>Create</c> writes — default options and every published
/// codec — the dmsdos driver must detect as the matching volume type and read back byte-exact, and an
/// edit (add, remove) must leave a volume it still reads.
/// </summary>
[TestFixture]
public sealed class CvfCreateDmsdosTests {

  private static (string Name, byte[] Data)[] Files() {
    var random = new byte[5000];
    new Random(13).NextBytes(random);
    return [
      ("TEXT.TXT", CvfSamples.Get("text-32k")[..20000]),
      ("RANDOM.BIN", random),
      ("TINY.TXT", "tiny"u8.ToArray()),
    ];
  }

  private static IEnumerable<TestCaseData> Cases() {
    (Func<IArchiveCreatable> Make, string Detects, string[] Methods)[] formats = [
      (() => new DoubleSpaceFormatDescriptor(), "drivespace CVF (version 2)", ["stored", "ds", "jm", "auto++"]),
      (() => new DriveSpaceFormatDescriptor(), "drivespace CVF (version 2)", ["stored", "jm+", "auto"]),
      (() => new DriveSpace3FormatDescriptor(), "drivespace 3 CVF", ["stored", "jm", "sq", "ds", "auto++"]),
      (() => new StackerFormatDescriptor(), "stacker version 3 CVF", ["stored", "sd3", "sd3++", "auto"]),
    ];
    foreach (var (make, detects, methods) in formats) {
      var id = ((IFormatDescriptor)make()).Id;
      yield return new TestCaseData(make, detects, null).SetName($"{{m}}({id}, default)");
      foreach (var method in methods)
        yield return new TestCaseData(make, detects, method).SetName($"{{m}}({id}, {method})");
    }
  }

  private static byte[] Create(IArchiveCreatable descriptor, string? method) {
    using var ms = new MemoryStream();
    descriptor.Create(ms, [.. Files().Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))],
      new FormatCreateOptions { MethodName = method });
    return ms.ToArray();
  }

  private static void AssertDmsdosReads(byte[] image, string detects, (string Name, byte[] Data)[] files) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb-cvf-create-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(path, image);
      Assert.That(DmsdosOracle.Detect(path), Does.Contain(detects));
      foreach (var (name, expected) in files) {
        var (exit, bytes) = DmsdosOracle.ReadFile(path, name);
        Assert.That(exit, Is.Zero, $"dcread failed for {name}");
        Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(expected.Length), $"short read for {name}");
        Assert.That(bytes[..expected.Length], Is.EqualTo(expected), name);
      }
    } finally {
      File.Delete(path);
    }
  }

  [Test, Category("DriverProof"), TestCaseSource(nameof(Cases))]
  public void GivenAVolumeTheDescriptorCreated_WhenReadByDmsdos_ThenEveryFileIsByteExact(Func<IArchiveCreatable> make, string detects, string? method) {
    DmsdosOracle.Require();

    AssertDmsdosReads(Create(make(), method), detects, Files());
  }

  [Test, Category("DriverProof"), TestCaseSource(nameof(Cases))]
  public void GivenAnEditedVolume_WhenReadByDmsdos_ThenTheSurvivorsAreByteExact(Func<IArchiveCreatable> make, string detects, string? method) {
    DmsdosOracle.Require();
    var descriptor = make();
    using var image = new MemoryStream();
    image.Write(Create(descriptor, method));
    var added = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("added later\r\n", 300)));

    ((IArchiveModifiable)descriptor).Add(image, [ArchiveInputInfo.InMemory("ADDED.TXT", added)]);
    ((IArchiveModifiable)descriptor).Remove(image, ["RANDOM.BIN"]);

    AssertDmsdosReads(image.ToArray(), detects, [Files()[0], Files()[2], ("ADDED.TXT", added)]);
  }
}
