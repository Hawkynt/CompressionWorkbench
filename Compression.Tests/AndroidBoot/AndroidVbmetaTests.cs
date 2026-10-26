#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Compression.Registry;
using FileFormat.AndroidVbmeta;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.AndroidBoot;

/// <summary>
/// vbmeta images: two written by <c>avbtool</c> (see Firmware/ReferenceVectors/README.md) are read,
/// verified and — for the unsigned one — rewritten byte for byte; unsigned images we write must
/// match <c>avbtool make_vbmeta_image --algorithm NONE</c> and pass its <c>info_image</c>.
/// </summary>
[TestFixture]
public sealed class AndroidVbmetaTests {

  private static readonly AndroidVbmetaFormatDescriptor Descriptor = new();

  private static Dictionary<string, byte[]> Extract(byte[] image) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_vbm_" + Guid.NewGuid().ToString("N")[..10]);
    try {
      using (var ms = new MemoryStream(image)) Descriptor.Extract(ms, dir, null, null);
      return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  private static byte[] Create(IEnumerable<(string Name, byte[] Data)> inputs) {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [.. inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  private static string Ini(byte[] image) => Encoding.UTF8.GetString(Extract(image)["metadata.ini"]);

  // ── avbtool-written vectors ──────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAvbtoolsSignedVbmeta_WhenListed_ThenHashAndRsaSignatureVerify() {
    var ini = Ini(Firmware.FirmwareVectors.Load("signed.vbmeta"));
    Assert.Multiple(() => {
      Assert.That(ini, Does.Contain("algorithm = SHA256_RSA2048"));
      Assert.That(ini, Does.Contain("hash_valid = true"));
      Assert.That(ini, Does.Contain("signature_valid = true"));
      Assert.That(ini, Does.Contain("rollback_index = 7"));
      Assert.That(ini, Does.Contain("release_string = avbtool 1.4.0"));
      // the values avbtool info_image printed for this image
      Assert.That(ini, Does.Contain("public_key_sha1 = cdbb77177f731920bbe0a0f94f84d9038ae0617d"));
      Assert.That(ini, Does.Contain("digest = bf2c275accee5b56c848d7708341b4d2f244b2eebdac087f56c2ad65bbe9cd6c"));
      Assert.That(ini, Does.Contain("cmdline = androidboot.cwb=1"));
      Assert.That(ini, Does.Contain("key = com.cwb.test\nvalue = yes"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAvbtoolsSignedVbmeta_WhenExtracted_ThenEachDescriptorAndTheKeyAreMembers() {
    var files = Extract(Firmware.FirmwareVectors.Load("signed.vbmeta"));
    Assert.That(files.Keys, Is.SupersetOf(new[] {
      "metadata.ini", "descriptors/00_chain_partition_system.bin", "descriptors/01_property_com.cwb.test.bin",
      "descriptors/02_kernel_cmdline.bin", "descriptors/03_hash_boot.bin", "public_key.bin", "signature.bin",
    }));
    Assert.That(files["public_key.bin"].Length, Is.EqualTo(8 + 2 * 256));
  }

  [Test, Category("Exception")]
  public void GivenASignedVbmetaWithOneFlippedDescriptorByte_WhenListed_ThenTheHashAndSignatureFail() {
    var image = Firmware.FirmwareVectors.Load("signed.vbmeta");
    image[256 + 320 + 100] ^= 0x01; // inside the auxiliary block
    var ini = Ini(image);
    Assert.That(ini, Does.Contain("hash_valid = false").And.Contain("signature_valid = false"));
  }

  [Test, Category("Exception")]
  public void GivenASignedVbmeta_WhenEdited_ThenTheEditIsRefusedAndTheImageUntouched() {
    var image = Firmware.FirmwareVectors.Load("signed.vbmeta");
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(() => Descriptor.Remove(ms, ["descriptors/02_kernel_cmdline.bin"]), Throws.TypeOf<NotSupportedException>());
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("RoundTrip")]
  public void GivenAvbtoolsUnsignedVbmeta_WhenExtractedAndRecreated_ThenTheBytesAreIdentical() {
    var image = Firmware.FirmwareVectors.Load("unsigned.vbmeta");
    var rebuilt = Create(Extract(image).Select(kv => (kv.Key, kv.Value)));
    Assert.That(rebuilt, Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void GivenAvbtoolsUnsignedVbmeta_WhenADescriptorIsRemoved_ThenTheRestStayAndTheBlockShrinks() {
    var image = Firmware.FirmwareVectors.Load("unsigned.vbmeta");
    using var ms = new MemoryStream();
    ms.Write(image);
    Descriptor.Remove(ms, ["descriptors/00_property_com.cwb.test.bin"]);
    var edited = AvbVbmeta.Parse(ms.ToArray());
    var original = AvbVbmeta.Parse(image);
    Assert.Multiple(() => {
      Assert.That(edited.Descriptors.Select(d => d.Kind), Is.EqualTo(original.Descriptors.Skip(1).Select(d => d.Kind)));
      Assert.That(edited.RollbackIndex, Is.EqualTo(3));
      Assert.That(edited.IsCanonical, Is.True);
    });
  }

  // ── malformed / boundary ─────────────────────────────────────────────

  [Test, Category("Exception")]
  public void GivenATruncatedVbmeta_WhenListed_ThenItFallsBackWithoutThrowing() {
    var image = Firmware.FirmwareVectors.Load("signed.vbmeta")[..1000];
    using var ms = new MemoryStream(image);
    Assert.That(Descriptor.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "FULL.img" }));
  }

  [Test, Category("Exception")]
  public void GivenADescriptorLengthPastItsBlock_WhenParsed_ThenItIsRejected() {
    var image = Firmware.FirmwareVectors.Load("unsigned.vbmeta");
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(256 + 8), 1_000_000);
    Assert.That(() => AvbVbmeta.Parse(image), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("Boundary")]
  public void GivenNoInputs_WhenCreated_ThenTheResultIsABareHeader() {
    var image = Create([]);
    var parsed = AvbVbmeta.Parse(image);
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(256));
      Assert.That(parsed.Descriptors, Is.Empty);
      Assert.That(parsed.AlgorithmType, Is.Zero);
    });
  }

  [Test, Category("Exception")]
  public void GivenAPublicKeyAmongTheInputs_WhenCreated_ThenSigningIsRefused()
    => Assert.That(() => Create([("public_key.bin", new byte[520])]), Throws.TypeOf<NotSupportedException>());

  [Test, Category("Exception")]
  public void GivenAMalformedDescriptorBlob_WhenCreated_ThenItIsRefused()
    => Assert.That(() => Create([("descriptors/00_x.bin", new byte[20])]), Throws.ArgumentException);

  // ── write constraints ────────────────────────────────────────────────

  [TestCase("descriptors/00_hash.bin")]
  [TestCase("descriptors\\03_property.bin")]
  [TestCase("public_key_metadata.bin")]
  [TestCase("trailer.bin")]
  [TestCase("METADATA.INI")]
  public void GivenAMemberCreateWrites_WhenOffered_ThenItIsAccepted(string name) {
    Assert.That(Descriptor.CanAccept(ArchiveInputInfo.InMemory(name, [1]), out var reason), Is.True);
    Assert.That(reason, Is.Null);
  }

  [TestCase("PROBE.BIN")]
  [TestCase("descriptors/hash.bin")]
  [TestCase("00_hash.bin")]
  [TestCase("FULL.img")]
  public void GivenANameThatIsNoVbmetaMember_WhenOffered_ThenItIsDeclinedWithTheMemberList(string name) {
    Assert.That(Descriptor.CanAccept(ArchiveInputInfo.InMemory(name, [1]), out var reason), Is.False);
    Assert.That(reason, Does.Contain("descriptors/NN_*.bin"));
    Assert.That(() => Create([(name, new byte[] { 1 })]), Throws.ArgumentException, "Create refuses what CanAccept declines");
  }

  [TestCase("public_key.bin")]
  [TestCase("signature.bin")]
  public void GivenAKeyOrSignature_WhenOffered_ThenItIsDeclinedBecauseSigningNeedsThePrivateKey(string name) {
    Assert.That(Descriptor.CanAccept(ArchiveInputInfo.InMemory(name, [1]), out var reason), Is.False);
    Assert.That(reason, Does.Contain("private key"));
  }

  // ── avbtool oracle ───────────────────────────────────────────────────

  private static readonly Lazy<string[]?> Avbtool = new(() => {
    var configured = Environment.GetEnvironmentVariable("CWB_AVBTOOL");
    var candidates = new List<string[]>();
    if (!string.IsNullOrWhiteSpace(configured)) {
      candidates.Add(["python3", "-c", AndroidBootImageMkbootimgOracleTests.RunScript, configured]);
      candidates.Add(["python", "-c", AndroidBootImageMkbootimgOracleTests.RunScript, configured]);
    }
    candidates.Add(["avbtool"]);
    foreach (var c in candidates)
      try {
        if (Run(c, ["version"]).ExitCode == 0) return c;
      } catch (Win32Exception) {
        // not there
      }
    return null;
  });

  private static (int ExitCode, string StdOut, string StdErr) Run(string[] tool, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(tool[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in tool.Skip(1).Concat(arguments)) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }

  [Test, Category("ArchiveExternal")]
  public void GivenDescriptorsAvbtoolWrote_WhenWeWriteAnUnsignedImage_ThenAvbtoolWritesTheSameBytesAndReadsOurs() {
    if (Avbtool.Value is not { } avbtool) {
      Assert.Ignore("avbtool not found (set CWB_AVBTOOL to avbtool.py from platform/external/avb).");
      return;
    }
    var dir = Path.Combine(Path.GetTempPath(), "cwb_avbtool_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var reference = Path.Combine(dir, "reference.vbmeta");
      var (code, _, stderr) = Run(avbtool, ["make_vbmeta_image", "--output", reference, "--algorithm", "NONE",
        "--prop", "a.b:c", "--prop", "empty:", "--kernel_cmdline", "quiet splash", "--rollback_index", "42",
        "--rollback_index_location", "2", "--padding_size", "4096"]);
      Assert.That(code, Is.Zero, stderr);
      var expected = File.ReadAllBytes(reference);

      var ours = Create(Extract(expected).Select(kv => (kv.Key, kv.Value)));
      Assert.That(ours, Is.EqualTo(expected), "rebuilding avbtool's unsigned image from its members changed the bytes");

      // A descriptor added by us, read back by avbtool.
      using var ms = new MemoryStream();
      ms.Write(expected);
      var extra = AvbVbmeta.Parse(expected).Descriptors[0].Encoded;
      Descriptor.Add(ms, [ArchiveInputInfo.InMemory("descriptors/03_property_copy.bin", extra)]);
      var edited = Path.Combine(dir, "edited.vbmeta");
      File.WriteAllBytes(edited, ms.ToArray());
      var (infoCode, info, infoErr) = Run(avbtool, ["info_image", "--image", edited]);
      Assert.That(infoCode, Is.Zero, infoErr);
      Assert.That(info.Split("Prop: a.b -> 'c'").Length - 1, Is.EqualTo(2), info);
      Assert.That(info, Does.Contain("Rollback Index:           42"));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
