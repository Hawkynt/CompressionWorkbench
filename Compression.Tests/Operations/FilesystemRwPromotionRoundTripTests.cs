#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations;

[TestFixture]
public sealed class FilesystemRwPromotionRoundTripTests {
  [TestCase("CramFs")]
  [TestCase("SquashFs")]
  [TestCase("Erofs")]
  [TestCase("Msa")]
  [TestCase("Pfs0")]
  [Category("HappyPath"), Category("RoundTrip")]
  public void CreateAddRemove_PromotedFormatsKeepSurvivorsByteExact(string formatId) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(formatId);
    Assert.That(ops, Is.Not.Null, formatId);
    Assert.That(ops, Is.InstanceOf<IArchiveCreatable>(), formatId);
    Assert.That(ops, Is.InstanceOf<IArchiveModifiable>(), formatId);

    var descriptor = FormatRegistry.All.Single(d => d.Id == formatId);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True, formatId);

    var creator = (IArchiveCreatable)ops!;
    var modifier = (IArchiveModifiable)ops;
    var archiveOps = (IArchiveFormatOperations)ops;
    var a = Enumerable.Range(0, 333).Select(i => (byte)(i * 7)).ToArray();
    var b = Enumerable.Range(0, 517).Select(i => (byte)(i * 11)).ToArray();
    var c = Enumerable.Range(0, 129).Select(i => (byte)(255 - i)).ToArray();

    using var image = new MemoryStream();
    creator.Create(image, [
      ArchiveInputInfo.InMemory("A.TXT", a),
      ArchiveInputInfo.InMemory("B.BIN", b),
    ], new FormatCreateOptions());
    Assert.That(image.Length, Is.GreaterThan(0), formatId);

    image.Position = 0;
    modifier.Add(image, [ArchiveInputInfo.InMemory("C.DAT", c)]);
    Assert.That(ListNames(archiveOps, image), Does.Contain("C.DAT").IgnoreCase, formatId);

    image.Position = 0;
    modifier.Remove(image, ["B.BIN"]);
    var names = ListNames(archiveOps, image);
    Assert.Multiple(() => {
      Assert.That(names.Any(n => Matches(n, "A.TXT")), Is.True, $"{formatId}: A.TXT disappeared");
      Assert.That(names.Any(n => Matches(n, "B.BIN")), Is.False, $"{formatId}: B.BIN survived removal");
      Assert.That(names.Any(n => Matches(n, "C.DAT")), Is.True, $"{formatId}: C.DAT disappeared");
    });

    var work = Path.Combine(Path.GetTempPath(), "cwb_rw_promote_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try {
      image.Position = 0;
      archiveOps.Extract(image, work, null, null);
      Assert.That(FindExtracted(work, "A.TXT"), Is.EqualTo(a), $"{formatId}: A.TXT changed");
      Assert.That(FindExtracted(work, "C.DAT"), Is.EqualTo(c), $"{formatId}: C.DAT changed");
    } finally {
      try { Directory.Delete(work, recursive: true); } catch { }
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Ewf_ReplaceAndClearSemanticMedia_RoundTrips() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("Ewf")!;
    var descriptor = FormatRegistry.All.Single(d => d.Id == "Ewf");
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(ops, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(ops, Is.InstanceOf<IArchiveModifiable>());

    var original = Enumerable.Range(0, 300_123).Select(i => (byte)(i * 13)).ToArray();
    var replacement = Enumerable.Range(0, 130_777).Select(i => (byte)(i * 17)).ToArray();
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image,
      [ArchiveInputInfo.InMemory("capture.raw", original)], new FormatCreateOptions());

    Assert.That(ReadNamed((IArchiveFormatOperations)ops, image, "media.raw"), Is.EqualTo(original));

    image.Position = 0;
    ((IArchiveModifiable)ops).Add(image, [ArchiveInputInfo.InMemory("media.raw", replacement)]);
    Assert.That(ReadNamed((IArchiveFormatOperations)ops, image, "media.raw"), Is.EqualTo(replacement));

    image.Position = 0;
    ((IArchiveModifiable)ops).Remove(image, ["media.raw"]);
    var names = ListNames((IArchiveFormatOperations)ops, image);
    Assert.That(names.Any(n => Matches(n, "media.raw")), Is.True,
      "An empty EWF still represents a zero-length acquired medium.");
    Assert.That(ReadNamed((IArchiveFormatOperations)ops, image, "media.raw"), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Refs_AdvertisesScopedOfflineMutationAndFailsClosedOnDiagnosticImage() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("Refs")!;
    var descriptor = FormatRegistry.All.Single(d => d.Id == "Refs");
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(ops, Is.InstanceOf<IArchiveModifiable>());

    // A header-only diagnostic image has no live namespace. The modifier must
    // reject it before touching bytes rather than treating FULL.refs as content.
    var imageBytes = BuildMinimalRefsHeader();
    using var image = new MemoryStream(imageBytes, writable: true);
    var before = image.ToArray();
    Assert.Throws<InvalidDataException>(() =>
      ((IArchiveModifiable)ops).Add(image, [ArchiveInputInfo.InMemory("A.TXT", [1, 2, 3])]));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  private static List<string> ListNames(IArchiveFormatOperations ops, MemoryStream image) {
    image.Position = 0;
    return ops.List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
  }

  private static byte[] ReadNamed(IArchiveFormatOperations ops, MemoryStream image, string name) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_rw_extract_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try {
      image.Position = 0;
      ops.Extract(image, work, null, [name]);
      return FindExtracted(work, name);
    } finally {
      try { Directory.Delete(work, recursive: true); } catch { }
    }
  }

  private static byte[] FindExtracted(string root, string name) {
    var file = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
      .FirstOrDefault(f => Matches(Path.GetFileName(f), name));
    Assert.That(file, Is.Not.Null, $"Expected extracted file {name}");
    return File.ReadAllBytes(file!);
  }

  private static bool Matches(string actual, string expected)
    => string.Equals(Path.GetFileName(actual.Replace('\\', '/')), expected, StringComparison.OrdinalIgnoreCase);

  private static byte[] BuildMinimalRefsHeader() {
    var image = new byte[4096];
    System.Text.Encoding.ASCII.GetBytes("ReFS").CopyTo(image.AsSpan(3));
    System.Text.Encoding.ASCII.GetBytes("FSRS").CopyTo(image.AsSpan(0x10));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x14, 2), 0x200);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18, 8), 1024UL);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20, 4), 512);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x24, 4), 8);
    image[0x28] = 3;
    image[0x29] = 14;
    return image;
  }
}