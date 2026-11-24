using FileFormat.Qcow2;

namespace Compression.Tests.Qcow2;

/// <summary>
/// In-place edits of every QCOW2 profile qemu-img makes: compressed (deflate and zstd) clusters,
/// extended L2 entries, refcount widths from 1 to 64 bits, overlays over QCOW2 and raw backing
/// files, and LUKS encryption with an external data file. qemu-img makes each image, we edit it,
/// and qemu-img must find it consistent (<c>check</c>) and read exactly the edited guest
/// (<c>compare</c>); a backing file must come out byte for byte as it went in.
/// </summary>
[TestFixture]
public sealed class Qcow2EditProfileTests {
  private const string Password = "Secr3t";

  /// <summary>
  /// The edits: inside a cluster, across a sector and a cluster boundary, a run over several
  /// clusters, single bytes far out; each skipped when the guest is too small for it.
  /// </summary>
  internal static byte[] Edit(string path, string? password, byte[] before) {
    var expected = (byte[])before.Clone();
    using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
    using var guest = Qcow2Stream.Open(file, password);
    Assert.That(guest.CanWrite, Is.True, "precondition: the image is writable");
    foreach (var (offset, length, value) in new (int, int, byte)[] {
               (100, 300, 0xC1), (4090, 20, 0xC2), (20000, 50_000, 0xC3), (131_071, 2, 0xC4), (200_000, 1, 0xC5), (250_000, 6_000, 0xC6) }) {
      if (offset + length > expected.Length) continue;
      var patch = Enumerable.Range(0, length).Select(i => (byte)(value ^ i)).ToArray();
      guest.Position = offset;
      guest.Write(patch);
      patch.CopyTo(expected, offset);
    }
    guest.Flush();
    return expected;
  }

  private static void RequireQemu() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("qemu-img") || !FsInteropToolbox.WslHasTool("qemu-io"))
      Assert.Ignore("qemu-img and qemu-io are needed in WSL (sudo apt install -y qemu-utils).");
  }

  private static byte[] Guest() {
    var guest = new byte[256 * 1024];
    var text = "qcow2 edit profile "u8;
    for (var i = 0; i < 131072; ++i) guest[i] = text[i % text.Length];
    new Random(17).NextBytes(guest.AsSpan(196608));
    return guest;
  }

  private const string Secret = "--object secret,id=s0,data=" + Password;
  private const string EncOpts = "'driver=qcow2,file.filename=image.qcow2,encrypt.key-secret=s0'";

  /// <summary>How qemu-img/qemu-io make each profile from guest.raw into image.qcow2 (and its backing or data file).</summary>
  private static IEnumerable<TestCaseData> Profiles() {
    yield return new TestCaseData("zstd", "qemu-img convert -c -f raw -O qcow2 -o compression_type=zstd,cluster_size=4096 guest.raw image.qcow2", new string[0], false);
    yield return new TestCaseData("deflate", "qemu-img convert -c -f raw -O qcow2 -o cluster_size=4096 guest.raw image.qcow2", new string[0], false);
    yield return new TestCaseData("extl2",
      "qemu-img create -q -f qcow2 -o extended_l2=on,cluster_size=16384 image.qcow2 256K && " +
      "qemu-io -f qcow2 -c 'write -P 0x11 0 2048' -c 'write -P 0x22 5120 1024' -c 'write -z 16384 4096' -c 'write -P 0x33 40960 16384' -c 'write -z 65536 16384' image.qcow2",
      new string[0], false);
    foreach (var bits in new[] { 1, 2, 4, 8, 32, 64 })
      yield return new TestCaseData($"refcount{bits}", $"qemu-img convert -f raw -O qcow2 -o refcount_bits={bits},cluster_size=4096 guest.raw image.qcow2", new string[0], false);
    yield return new TestCaseData("backing-qcow2",
      "qemu-img convert -f raw -O qcow2 -o cluster_size=4096 guest.raw base.qcow2 && " +
      "qemu-img create -q -f qcow2 -b base.qcow2 -F qcow2 -o cluster_size=4096 image.qcow2 256K && " +
      "qemu-io -f qcow2 -c 'write -P 0x55 8192 4096' -c 'write -z 135168 4096' image.qcow2",
      new[] { "base.qcow2" }, false);
    yield return new TestCaseData("backing-extl2",
      "qemu-img convert -f raw -O qcow2 -o cluster_size=4096 guest.raw base.qcow2 && " +
      "qemu-img create -q -f qcow2 -b base.qcow2 -F qcow2 -o extended_l2=on,cluster_size=16384 image.qcow2 256K && " +
      "qemu-io -f qcow2 -c 'write -P 0x66 1024 512' -c 'write -P 0x77 33792 1024' image.qcow2",
      new[] { "base.qcow2" }, false);
    yield return new TestCaseData("backing-raw",
      "cp guest.raw base.raw && qemu-img create -q -f qcow2 -b base.raw -F raw -o cluster_size=4096 image.qcow2 256K && " +
      "qemu-io -f qcow2 -c 'write -P 0x99 4096 4096' image.qcow2",
      new[] { "base.raw" }, false);
    yield return new TestCaseData("luks-datafile",
      $"qemu-img create -q -f qcow2 {Secret} -o encrypt.format=luks,encrypt.key-secret=s0,encrypt.iter-time=10,data_file=image.data,cluster_size=4096 image.qcow2 256K && " +
      $"qemu-io {Secret} --image-opts {EncOpts} -c 'write -P 0x11 0 10000' -c 'write -P 0x22 65536 4096' -c 'write -P 0x33 131072 70000'",
      new[] { "image.data" }, true);
  }

  [TestCaseSource(nameof(Profiles))]
  [Category("ExternalInterop")]
  public void Given_AQemuImage_When_WeEditItInPlace_Then_QemuImgChecksItAndReadsTheEdits(string profile, string make, string[] companions, bool encrypted) {
    RequireQemu();
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-edit-").FullName;
    try {
      File.WriteAllBytes(Path.Combine(dir, "guest.raw"), Guest());
      var w = FsInteropToolbox.WinToWsl(dir);
      var made = FsInteropToolbox.RunWsl($"cd {w} && {make} && qemu-img check -q {(encrypted ? $"{Secret} --image-opts {EncOpts}" : "image.qcow2")}");
      Assert.That(made.ExitCode, Is.Zero, $"precondition: qemu-img made the {profile} image\n{made.StdOut}{made.StdErr}");

      var path = Path.Combine(dir, "image.qcow2");
      var password = encrypted ? Password : null;
      byte[] before;
      using (var file = File.OpenRead(path))
      using (var reader = new Qcow2Reader(file, password))
        before = reader.ExtractDisk();
      var backingBefore = companions.Where(c => !c.EndsWith(".data", StringComparison.Ordinal))
        .ToDictionary(c => c, c => File.ReadAllBytes(Path.Combine(dir, c)));

      File.WriteAllBytes(Path.Combine(dir, "expected.raw"), Edit(path, password, before));

      var image = encrypted ? $"{Secret} --image-opts {EncOpts}" : "image.qcow2";
      var compareImage = encrypted ? $"{Secret} --image-opts {EncOpts} 'driver=raw,file.filename=expected.raw'" : "-f qcow2 -F raw image.qcow2 expected.raw";
      var judged = FsInteropToolbox.RunWsl($"cd {w} && qemu-img check {image} && qemu-img compare {compareImage}");
      Assert.That(judged.ExitCode, Is.Zero, $"{profile}: qemu-img rejected our edit\n{judged.StdOut}{judged.StdErr}");
      foreach (var (name, bytes) in backingBefore)
        Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(bytes), $"{profile}: the backing file {name} is never written");
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
