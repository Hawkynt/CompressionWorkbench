#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Mfs1;

namespace Compression.Tests.Mfs1;

/// <summary>
/// In-place modify tests for the Acorn MFS-1 (DFS-tier) descriptor. MFS-1's
/// catalog is a flat fixed-offset region (sector 0 names + sector 1 metadata)
/// that the in-place modifier rebuilds via <see cref="Mfs1Writer"/>; the
/// outer sector count is preserved across Add/Remove operations.
/// </summary>
[TestFixture]
public class Mfs1InPlaceModifyTests {

  private static MemoryStream BuildBaselineImage(params (string Name, string Body)[] entries) {
    var w = new Mfs1Writer();
    foreach (var (name, body) in entries)
      w.AddFile(name, Encoding.ASCII.GetBytes(body));
    var image = w.Build();
    return new MemoryStream(image, writable: true) { Capacity = image.Length };
  }

  private static IReadOnlyList<string> ListNames(Stream archive) {
    archive.Position = 0;
    using var r = new Mfs1Reader(archive);
    return r.Entries.Select(e => e.Name).ToList();
  }

  private static byte[] ReadEntry(Stream archive, string name) {
    archive.Position = 0;
    using var r = new Mfs1Reader(archive);
    var e = r.Entries.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    return r.Extract(e);
  }

  // ── Writer sanity (precondition for the modifier) ──────────────────────

  [Test, Category("RoundTrip")]
  public void Writer_RoundTripsThroughReader() {
    using var image = BuildBaselineImage(("ALPHA", "AAA"), ("BETA", "BBB"));
    var names = ListNames(image);
    Assert.That(names, Does.Contain("ALPHA"));
    Assert.That(names, Does.Contain("BETA"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "ALPHA")), Is.EqualTo("AAA"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "BETA")), Is.EqualTo("BBB"));
  }

  // ── Add ─────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_NewFile_AppearsViaReader() {
    using var image = BuildBaselineImage(("ALPHA", "AAA"));
    Mfs1InPlaceModifier.AddFiles(image, [("BETA", Encoding.ASCII.GetBytes("BBB"))]);
    Assert.That(ListNames(image), Does.Contain("BETA"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "BETA")), Is.EqualTo("BBB"));
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesExistingFile() {
    using var image = BuildBaselineImage(("KEEP", "KEEPMSG"));
    var keepBefore = ReadEntry(image, "KEEP");
    Mfs1InPlaceModifier.AddFiles(image, [("NEW", new byte[] { 9, 9, 9 })]);
    var keepAfter = ReadEntry(image, "KEEP");
    Assert.That(keepAfter, Is.EqualTo(keepBefore));
  }

  [Test, Category("RoundTrip")]
  public void Add_DoesNotChangeImageSize() {
    using var image = BuildBaselineImage(("A", "AAA"));
    var sizeBefore = image.Length;
    Mfs1InPlaceModifier.AddFiles(image, [("B", new byte[] { 1, 2, 3 })]);
    Assert.That(image.Length, Is.EqualTo(sizeBefore),
      "Add must preserve the MFS-1 image's outer sector count.");
  }

  // ── Remove ──────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_NamedFile_Disappears() {
    using var image = BuildBaselineImage(("A", "AAA"), ("B", "BBB"));
    var removed = Mfs1InPlaceModifier.RemoveFiles(image, ["A"]);
    Assert.That(removed, Is.EqualTo(1));
    Assert.That(ListNames(image), Does.Not.Contain("A"));
    Assert.That(ListNames(image), Does.Contain("B"));
  }

  [Test, Category("RoundTrip")]
  public void Remove_WipesPayloadBytes() {
    using var image = BuildBaselineImage(("SECRET", "MARKER-XYZZY-MFS1-WIPE-CHECK"));
    Mfs1InPlaceModifier.RemoveFiles(image, ["SECRET"]);
    var asAscii = Encoding.ASCII.GetString(image.ToArray());
    Assert.That(asAscii, Does.Not.Contain("MARKER-XYZZY-MFS1-WIPE-CHECK"),
      "Removed file's payload must not survive in the image.");
  }

  [Test, Category("RoundTrip")]
  public void Remove_UnknownName_NoOp() {
    using var image = BuildBaselineImage(("A", "AAA"));
    var removed = Mfs1InPlaceModifier.RemoveFiles(image, ["GHOST"]);
    Assert.That(removed, Is.EqualTo(0));
    Assert.That(ListNames(image), Does.Contain("A"));
  }

  // ── Descriptor interface routing ────────────────────────────────────────

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesCanCreateAndCanModify_AndImplementsInterfaces() {
    var d = new Mfs1FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ThenList_RoundTrips() {
    var d = new Mfs1FormatDescriptor();
    using var output = new MemoryStream();
    ((IArchiveCreatable)d).Create(output,
      [ArchiveInputInfo.InMemory("HELLO", Encoding.ASCII.GetBytes("hello-mfs1"))],
      new FormatCreateOptions());
    output.Position = 0;
    using var r = new Mfs1Reader(output);
    Assert.That(r.Entries.Select(e => e.Name), Does.Contain("HELLO"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_AndRemove() {
    var d = new Mfs1FormatDescriptor();
    using var image = BuildBaselineImage(("A", "AAA"));
    ((IArchiveModifiable)d).Add(image,
      [ArchiveInputInfo.InMemory("VIAIF", Encoding.ASCII.GetBytes("via-if"))]);
    Assert.That(ListNames(image), Does.Contain("VIAIF"));

    ((IArchiveModifiable)d).Remove(image, ["VIAIF"]);
    Assert.That(ListNames(image), Does.Not.Contain("VIAIF"));
  }

  // ── Mutate-then-extract roundtrip ───────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_Roundtrip() {
    using var image = BuildBaselineImage(("KEEP", "kept"));
    Mfs1InPlaceModifier.AddFiles(image, [("ADD1", Encoding.ASCII.GetBytes("payload-1"))]);
    Mfs1InPlaceModifier.AddFiles(image, [("ADD2", Encoding.ASCII.GetBytes("payload-2"))]);
    Mfs1InPlaceModifier.RemoveFiles(image, ["KEEP"]);

    var names = ListNames(image);
    Assert.That(names, Does.Contain("ADD1"));
    Assert.That(names, Does.Contain("ADD2"));
    Assert.That(names, Does.Not.Contain("KEEP"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "ADD1")), Is.EqualTo("payload-1"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "ADD2")), Is.EqualTo("payload-2"));
  }
}
