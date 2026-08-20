using Compression.Registry;

namespace Compression.Tests.ExFat;

/// <summary>
/// Defragmenting an exFAT volume must not lose a file, whichever way it packs
/// them.
/// </summary>
/// <remarks>
/// <para>An exFAT directory entry describes one contiguous run, so a file whose
/// destination is not contiguous cannot be expressed. The mover said so — but it
/// said it while relinking, which is after the data had been moved. The caller
/// caught the refusal and fell back to rebuilding the volume, and the volume it
/// rebuilt from was already half-migrated. The damage was then rebuilt
/// faithfully: files came back at full length holding other files' bytes, two of
/// them holding the same third file's.</para>
///
/// <para>Only the modes that fall back were affected, which made it look like a
/// defragmentation bug rather than a plan that was carried out before it was
/// checked.</para>
/// </remarks>
[TestFixture]
public class ExFatDefragIntegrityTests {

  private static (List<ArchiveInputInfo> Inputs, Dictionary<string, byte[]> Expected) Mixed(int totalBytes) {
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<ArchiveInputInfo>();

    void Add(string name, int length, int seed) {
      var data = new byte[length];
      for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + seed * 7 + (i >> 11));
      expected[name] = data;
      inputs.Add(ArchiveInputInfo.InMemory(name, data));
    }

    Add("BIG00001.BIN", totalBytes / 2, 1);
    var rest = totalBytes - totalBytes / 2;
    var perFile = rest / 32;
    for (var i = 0; i < 32; ++i) {
      var length = Math.Max(1, perFile + (i % 7) * 1024 - 3 * 1024);
      if (i % 11 == 0) length = 17 + i;
      Add($"F{i:D4}.BIN", length, i + 2);
    }
    return (inputs, expected);
  }

  [Test, Category("Regression")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void DefragmentKeepsEveryFile(DefragMode mode) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("ExFat")!;
    var (inputs, expected) = Mixed(8 * 1024 * 1024);

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    // Fragment it: a freshly created volume has nothing out of place, the
    // in-place planner handles it, and the path that lost data is never reached.
    var doomed = expected.Keys.Where(k => k.StartsWith('F')).Where((_, n) => n % 3 == 1).Take(12).ToArray();
    image.Position = 0;
    ((IArchiveModifiable)ops).Remove(image, doomed);
    foreach (var d in doomed) expected.Remove(d);

    var added = new List<ArchiveInputInfo>();
    for (var i = 0; i < 6; ++i) {
      var data = new byte[40 * 1024 + i * 977];
      for (var j = 0; j < data.Length; ++j) data[j] = (byte)(j * 31 + (900 + i) * 7 + (j >> 11));
      expected[$"ADD{i:D2}.BIN"] = data;
      added.Add(ArchiveInputInfo.InMemory($"ADD{i:D2}.BIN", data));
    }
    image.Position = 0;
    ((IArchiveModifiable)ops).Add(image, added);

    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions { Mode = mode });

    using var read = new MemoryStream(image.ToArray());
    var reader = new FileSystem.ExFat.ExFatReader(read);
    foreach (var (name, want) in expected) {
      var entry = reader.Entries.FirstOrDefault(e =>
        !e.IsDirectory && string.Equals(Path.GetFileName(e.Name), name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{mode}: {name} is missing after defragmenting");
      Assert.That(reader.Extract(entry!), Is.EqualTo(want),
        $"{mode}: {name} did not read back byte for byte");
    }
  }
}
