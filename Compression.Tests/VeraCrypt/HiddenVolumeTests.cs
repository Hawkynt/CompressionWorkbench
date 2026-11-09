using Compression.Lib;
using Compression.Mounting;
using Compression.Registry;
using Compression.Tests.Luks;
using FileFormat.VeraCrypt;

namespace Compression.Tests.VeraCrypt;

[TestFixture]
public sealed class HiddenVolumeTests {
  private const string Outer = "outer password", Hidden = "hidden password";
  private static readonly byte[] OuterContent = Enumerable.Range(0, 3000).Select(static i => (byte)(i * 5)).ToArray();
  private static readonly byte[] HiddenContent = Enumerable.Range(0, 2500).Select(static i => (byte)(i * 11 + 1)).ToArray();

  [OneTimeSetUp]
  public void RegisterFormats() => FormatRegistration.EnsureInitialized();

  private static readonly Dictionary<string, string> Fast = new() { ["Pim"] = "1", ["Prf"] = "sha512" };

  /// <summary>An outer FAT volume of 4 MiB with a hidden FAT volume at its end, made through the descriptor.</summary>
  internal static byte[] CreateWithHidden(out byte[] hiddenImage) {
    hiddenImage = LuksMountTests.FatImage("SECRET.TXT", HiddenContent);
    using var ms = new MemoryStream();
    new VeraCryptFormatDescriptor().Create(ms, [
      ArchiveInputInfo.InMemory("volume.img", LuksMountTests.FatImage("DECOY.TXT", OuterContent)),
      ArchiveInputInfo.InMemory("hidden.img", hiddenImage),
    ], new FormatCreateOptions {
      Password = Outer,
      FormatSpecific = new Dictionary<string, string> {
        ["Pim"] = "1", ["Size"] = (4 << 20).ToString(), ["HiddenPassword"] = Hidden, ["HiddenPim"] = "1", ["HiddenCipher"] = "Serpent-Twofish-AES",
      },
    });
    return ms.ToArray();
  }

  private static byte[] ReadFile(IFilesystemSession session, string name) {
    using var h = session.OpenFile(session.Lookup(session.RootNodeId, name)!.Value, FileAccess.Read);
    var buffer = new byte[h.Length];
    h.Read(0, buffer);
    return buffer;
  }

  [Test]
  public void GivenOuterAndHiddenVolumes_WhenEachPasswordIsUsed_ThenEachShowsItsOwnFilesystem() {
    var container = new MemoryStream(CreateWithHidden(out _));
    using (var outer = MountNamespaceResolver.Open("VeraCrypt", container, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), Outer, Fast)) {
      Assert.That(ReadFile(outer, "DECOY.TXT"), Is.EqualTo(OuterContent));
      Assert.That(outer.Lookup(outer.RootNodeId, "SECRET.TXT"), Is.Null);
    }
    var hidden = new Dictionary<string, string>(Fast) { ["Hidden"] = "true" };
    using (var inner = MountNamespaceResolver.Open("VeraCrypt", container, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), Hidden, hidden))
      Assert.That(ReadFile(inner, "SECRET.TXT"), Is.EqualTo(HiddenContent));
  }

  [Test]
  public void GivenAHiddenVolume_WhenItsHeaderIsRead_ThenItSitsWhereVeraCryptPutsIt() {
    var bytes = CreateWithHidden(out var image);
    using var s = new MemoryStream(bytes);
    var v = TcVolume.Open(s, Hidden, new() { Pim = 1, Header = TcHeaderKind.Hidden })!;
    Assert.Multiple(() => {
      Assert.That(v.HeaderOffset, Is.EqualTo(65536));
      Assert.That(v.DataOffset, Is.EqualTo(bytes.Length - 131072 - 4096 - image.Length));
      Assert.That(v.HiddenVolumeSize, Is.EqualTo((ulong)image.Length));
      Assert.That(v.Chain.Name, Is.EqualTo("serpent-twofish-aes"));
    });
    // The outer header says nothing about it.
    s.Position = 0;
    Assert.That(TcVolume.Open(s, Outer, new() { Pim = 1 })!.HiddenVolumeSize, Is.Zero);
  }

  [Test]
  public void GivenTheOuterPasswordAlone_WhenOpeningTheHiddenHeader_ThenNothingOpens() {
    using var s = new MemoryStream(CreateWithHidden(out _));
    Assert.That(TcVolume.Open(s, Outer, new() { Pim = 1, Header = TcHeaderKind.Hidden }), Is.Null);
    Assert.That(TcVolume.Open(s, Hidden, new() { Pim = 1, Header = TcHeaderKind.Standard }), Is.Null);
  }

  [Test]
  public void GivenOnlyThePassword_WhenMounting_ThenTheHiddenVolumeIsFoundAsVeraCryptFindsIt() {
    var container = new MemoryStream(CreateWithHidden(out _));
    using var inner = MountNamespaceResolver.Open("VeraCrypt", container, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true), Hidden, Fast);
    Assert.That(ReadFile(inner, "SECRET.TXT"), Is.EqualTo(HiddenContent));
  }

  [Test]
  public void GivenAProtectedOuterMount_WhenAWriteReachesTheHiddenArea_ThenItIsRefusedAndTheHiddenVolumeSurvives() {
    var bytes = CreateWithHidden(out var image);
    var container = new MemoryStream(bytes);
    var device = (HiddenProtectedBlockDevice)new VeraCryptFormatDescriptor().OpenBlockDevice(container,
      new(Outer, new Dictionary<string, string> { ["Pim"] = "1", ["ProtectHidden"] = Hidden, ["ProtectHiddenPim"] = "1" }), writable: true);
    using (device) {
      var sector = new byte[512];
      device.WriteBlocks(0, sector);                      // far from the hidden area: allowed
      var last = device.Geometry.BlockCount - 1;
      Assert.Throws<IOException>(() => device.WriteBlocks(last, sector));
      Assert.That(device.ProtectionTriggered, Is.True);
      Assert.Throws<IOException>(() => device.WriteBlocks(0, sector), "after triggering, every write is refused");
    }
    using var s = new MemoryStream(container.ToArray());
    var hidden = TcVolume.Open(s, Hidden, new() { Pim = 1, Header = TcHeaderKind.Hidden })!;
    using var hd = hidden.OpenBlockDevice(s, writable: false);
    var plain = new byte[hd.Geometry.Length];
    hd.ReadBlocks(0, plain);
    Assert.That(plain, Is.EqualTo(image));
  }

  [Test]
  public void GivenAWrongProtectionPassword_WhenMountingTheOuterVolume_ThenItIsRejected()
    => Assert.Throws<ArchivePasswordException>(() => new VeraCryptFormatDescriptor().OpenBlockDevice(new MemoryStream(CreateWithHidden(out _)),
      new(Outer, new Dictionary<string, string> { ["Pim"] = "1", ["ProtectHidden"] = "not it", ["ProtectHiddenPim"] = "1" }), writable: true));

  [Test]
  public void GivenAnExistingContainer_WhenAHiddenVolumeIsAdded_ThenBothVolumesOpen() {
    var outerImage = LuksMountTests.FatImage("DECOY.TXT", OuterContent);
    var container = new MemoryStream(VeraCryptTests.Create(outerImage, new TcCreateOptions { Pim = 1, VolumeSize = 2 << 20 }, Outer));
    var hiddenImage = LuksMountTests.FatImage("SECRET.TXT", HiddenContent);
    TcHiddenVolume.Add(container, Outer, new() { Pim = 1 }, hiddenImage, Hidden, new TcCreateOptions { Pim = 1, Cipher = "twofish" }, hiddenImage.Length);

    Assert.That(VeraCryptTests.Decrypt(container.ToArray(), Hidden, new() { Pim = 1, Header = TcHeaderKind.Hidden }), Is.EqualTo(hiddenImage));
    Assert.That(VeraCryptTests.Decrypt(container.ToArray(), Outer, new() { Pim = 1 }).AsSpan(0, outerImage.Length).SequenceEqual(outerImage), Is.True);
  }

  [Test]
  public void GivenAnOuterVolumeWithoutARecognisedFilesystem_WhenAddingAHiddenVolume_ThenRefused() {
    var container = new MemoryStream(VeraCryptTests.Create(LuksTests.SamplePayload(4096, 3), new TcCreateOptions { Pim = 1 }, Outer));
    Assert.Throws<InvalidOperationException>(() => TcHiddenVolume.Add(container, Outer, new() { Pim = 1 }, new byte[512], Hidden, new TcCreateOptions { Pim = 1 }, 4096));
  }

  [Test]
  public void GivenAHiddenVolumeThatWouldOverlapUsedOuterSpace_WhenAdding_ThenRefused() {
    var outerImage = LuksMountTests.FatImage("DECOY.TXT", OuterContent);
    var container = new MemoryStream(VeraCryptTests.Create(outerImage, new TcCreateOptions { Pim = 1, VolumeSize = outerImage.Length + 8192 }, Outer));
    Assert.Throws<InvalidOperationException>(() => TcHiddenVolume.Add(container, Outer, new() { Pim = 1 }, new byte[512], Hidden, new TcCreateOptions { Pim = 1 }, outerImage.Length));
  }

  [Test]
  public void GivenTheWrongOuterPassword_WhenAddingAHiddenVolume_ThenRefused() {
    var container = new MemoryStream(VeraCryptTests.Create(LuksMountTests.FatImage("A.TXT", [1]), new TcCreateOptions { Pim = 1, VolumeSize = 1 << 20 }, Outer));
    Assert.Throws<ArchivePasswordException>(() => TcHiddenVolume.Add(container, "nope", new() { Pim = 1, Prf = "sha512" }, new byte[512], Hidden, new TcCreateOptions { Pim = 1 }, 4096));
  }

  [Test]
  public void GivenTheSamePasswordForBoth_WhenCreating_ThenRefused()
    => Assert.Throws<ArgumentException>(() => new VeraCryptFormatDescriptor().Create(new MemoryStream(), [
      ArchiveInputInfo.InMemory("volume.img", new byte[512]), ArchiveInputInfo.InMemory("hidden.img", new byte[512]),
    ], new FormatCreateOptions { Password = Outer, FormatSpecific = new Dictionary<string, string> { ["HiddenPassword"] = Outer } }));
}

/// <summary>
/// VeraCrypt and cryptsetup as oracles for hidden volumes, both ways: VeraCrypt creates an outer
/// and a hidden volume and we open both; VeraCrypt mounts our hidden volume to our plaintext and
/// accepts it for hidden-volume protection of the outer one; cryptsetup's <c>--tcrypt-hidden</c>
/// maps ours too. Needs root for the mappings.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
[NonParallelizable]
public sealed class HiddenVolumeExternalConformanceTests {
  private const string Outer = "cwb outer volume password", Hidden = "cwb hidden volume password";
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-vch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void Require(string tool, string variable) {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool(tool)) return;
    var hint = $"{tool} is not installed.";
    if (Environment.GetEnvironmentVariable(variable) == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static (string Out, int Exit) Run(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command}\n-> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return (r.StdOut + r.StdErr, r.ExitCode);
  }

  private static string Sudo(string command) => $"echo 1234 | sudo -S -p '' {command}";
  private static string W(string path) => FsInteropToolbox.WinToWsl(path);

  private static (string Out, int Exit) MountVeraCrypt(string container, string password, string extra, string action) {
    var slot = 40 + Random.Shared.Next(24);
    const string device = "$(" + "echo 1234 | sudo -S -p '' veracrypt --text --list --slot=SLOT | awk '{print $3}')";
    return Run(Sudo($"veracrypt --text --non-interactive --mount {W(container)} --slot={slot} --filesystem=none --password='{password}' --pim=1 -k '' {extra}") +
               $" && dev={device.Replace("SLOT", slot.ToString(System.Globalization.CultureInfo.InvariantCulture))} && test -b \"$dev\" && " +
               Sudo($"sh -c '{action.Replace("MAPPER", "$dev")}'").Replace("sh -c '", "env dev=\"$dev\" sh -c '") +
               "; rc=$?; " + Sudo($"veracrypt --text --non-interactive --unmount {W(container)}") + "; exit $rc");
  }

  [Test]
  public void GivenVeraCryptsHiddenVolume_WhenWeOpenItAndTheOuterOne_ThenBothDecrypt() {
    Require("veracrypt", "CWB_REQUIRE_VERACRYPT");
    var container = Path.Combine(this._tmp, "theirs.hc");
    var rnd = Path.Combine(this._tmp, "rnd.bin");
    File.WriteAllBytes(rnd, LuksTests.SamplePayload(2048, 4));
    var (log, exit) = Run($"veracrypt --text --non-interactive --create {W(container)} --size=4M --password='{Outer}' --encryption=AES --hash=SHA-512 --pim=1 --filesystem=none --volume-type=normal -k '' --random-source={W(rnd)}" +
                          $" && veracrypt --text --non-interactive --create {W(container)} --size=1M --password='{Hidden}' --encryption=AES --hash=SHA-256 --pim=1 --filesystem=none --volume-type=hidden -k '' --random-source={W(rnd)}");
    Assert.That(exit, Is.EqualTo(0), log);
    var payload = LuksTests.SamplePayload(1024, 77);
    var plain = Path.Combine(this._tmp, "plain.bin");
    File.WriteAllBytes(plain, payload);
    (log, exit) = MountVeraCrypt(container, Hidden, "--protect-hidden=no", $"dd if={W(plain)} of=MAPPER bs=512 conv=notrunc,fsync status=none");
    Assert.That(exit, Is.EqualTo(0), log);

    var bytes = File.ReadAllBytes(container);
    Assert.That(VeraCryptTests.Decrypt(bytes, Hidden, new() { Pim = 1, Header = TcHeaderKind.Hidden }).AsSpan(0, payload.Length).SequenceEqual(payload), Is.True);
    using var s = new MemoryStream(bytes);
    Assert.That(TcVolume.Open(s, Outer, new() { Pim = 1 }), Is.Not.Null, "the outer volume must still open");
  }

  [Test]
  public void GivenOurHiddenVolume_WhenVeraCryptMountsItAndProtectsItFromTheOuter_ThenBothWork() {
    Require("veracrypt", "CWB_REQUIRE_VERACRYPT");
    var container = Path.Combine(this._tmp, "ours.hc");
    var hidden = LuksTests.SamplePayload(1024, 5);
    using (var fs = new FileStream(container, FileMode.Create, FileAccess.ReadWrite)) {
      TcVolumeWriter.Write(fs, [], Outer, new TcCreateOptions { Pim = 1, VolumeSize = 4 << 20 });
      var outer = TcVolume.Open(fs, Outer, new() { Pim = 1 })!;
      TcHiddenVolume.Write(fs, outer, 0, hidden, Hidden, new TcCreateOptions { Pim = 1, Prf = "sha256" }, hidden.Length);
    }
    var outFile = Path.Combine(this._tmp, "hidden.bin");
    var (log, exit) = MountVeraCrypt(container, Hidden, "--protect-hidden=no", $"dd if=MAPPER of={W(outFile)} bs=512 count={hidden.Length / 512} status=none");
    Assert.That(exit, Is.EqualTo(0), log);
    Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(hidden));

    (log, exit) = MountVeraCrypt(container, Outer, $"--protect-hidden=yes --protection-password='{Hidden}' --protection-pim=1 --protection-keyfiles=''", "test -b MAPPER");
    Assert.That(exit, Is.EqualTo(0), "VeraCrypt must accept our hidden header for protection: " + log);
  }

  [Test]
  public void GivenOurHiddenVolume_WhenCryptsetupMapsIt_ThenItsMappingIsOurPlaintext() {
    Require("cryptsetup", "CWB_REQUIRE_CRYPTSETUP");
    var container = Path.Combine(this._tmp, "ours.hc");
    var hidden = LuksTests.SamplePayload(512, 6);
    using (var fs = new FileStream(container, FileMode.Create, FileAccess.ReadWrite)) {
      TcVolumeWriter.Write(fs, [], Outer, new TcCreateOptions { Pim = 1, VolumeSize = 2 << 20 });
      TcHiddenVolume.Write(fs, TcVolume.Open(fs, Outer, new() { Pim = 1 })!, 0, hidden, Hidden, new TcCreateOptions { Pim = 1 }, hidden.Length);
    }
    var pass = Path.Combine(this._tmp, "pass.txt");
    File.WriteAllText(pass, Hidden);
    var outFile = Path.Combine(this._tmp, "hidden.bin");
    var name = "cwbh" + Guid.NewGuid().ToString("N")[..10];
    var (log, exit) = Run(Sudo($"sh -c 'cryptsetup tcryptOpen --veracrypt --veracrypt-pim 1 --tcrypt-hidden {W(container)} {name} < {W(pass)}'") +
                          " && " + Sudo($"sh -c 'cat /dev/mapper/{name} > {W(outFile)}'") + "; rc=$?; " + Sudo($"cryptsetup close {name}") + "; exit $rc");
    Assert.That(exit, Is.EqualTo(0), log);
    Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(hidden));
  }
}
