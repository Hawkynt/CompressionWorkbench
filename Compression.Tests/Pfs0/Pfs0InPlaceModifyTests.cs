#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Pfs0;

namespace Compression.Tests.Pfs0;

/// <summary>
/// In-place modify tests for PFS0. The format has a flat header + entry
/// table + string table + data layout that lets the modifier append/replace/
/// remove entries while keeping the buffer round-trippable through the
/// normal <see cref="Pfs0Reader"/>.
/// </summary>
[TestFixture]
public class Pfs0InPlaceModifyTests {

  private static MemoryStream BuildBaselineArchive(params (string Name, string Body)[] entries) {
    var ms = new MemoryStream();
    using (var w = new Pfs0Writer(ms, leaveOpen: true)) {
      foreach (var (name, body) in entries)
        w.AddEntry(name, Encoding.UTF8.GetBytes(body));
    }
    ms.Position = 0;
    return ms;
  }

  private static IReadOnlyList<string> ListNames(Stream archive) {
    archive.Position = 0;
    var r = new Pfs0Reader(archive, leaveOpen: true);
    return r.Entries.Select(e => e.Name).ToList();
  }

  private static byte[] ReadEntry(Stream archive, string name) {
    archive.Position = 0;
    var r = new Pfs0Reader(archive, leaveOpen: true);
    var e = r.Entries.Single(x => x.Name == name);
    return r.Extract(e);
  }

  // ── Add ─────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_NewEntry_RoundTripsThroughReader() {
    using var archive = BuildBaselineArchive(("a.bin", "AAA"), ("b.bin", "BBB"));
    Pfs0InPlaceModifier.AddFiles(archive, [("c.bin", Encoding.UTF8.GetBytes("CCC"))]);
    Assert.That(ListNames(archive), Is.EquivalentTo(new[] { "a.bin", "b.bin", "c.bin" }));
    Assert.That(Encoding.UTF8.GetString(ReadEntry(archive, "c.bin")), Is.EqualTo("CCC"));
  }

  [Test, Category("RoundTrip")]
  public void Add_ReplaceByName_OverwritesPayload() {
    using var archive = BuildBaselineArchive(("dup.bin", "OLD"));
    Pfs0InPlaceModifier.AddFiles(archive, [("dup.bin", Encoding.UTF8.GetBytes("NEW-LONGER"))]);
    Assert.That(ListNames(archive), Is.EquivalentTo(new[] { "dup.bin" }));
    Assert.That(Encoding.UTF8.GetString(ReadEntry(archive, "dup.bin")), Is.EqualTo("NEW-LONGER"));
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesExistingPayloads_ByteForByte() {
    using var archive = BuildBaselineArchive(("keep.bin", "INTACT-PAYLOAD-XYZ"), ("other.bin", "other-bytes"));
    var beforeKeep = ReadEntry(archive, "keep.bin");
    var beforeOther = ReadEntry(archive, "other.bin");

    Pfs0InPlaceModifier.AddFiles(archive, [("new.bin", new byte[] { 1, 2, 3 })]);

    var afterKeep = ReadEntry(archive, "keep.bin");
    var afterOther = ReadEntry(archive, "other.bin");
    Assert.That(afterKeep, Is.EqualTo(beforeKeep), "Existing payload must survive Add.");
    Assert.That(afterOther, Is.EqualTo(beforeOther), "Other payload must survive Add.");
  }

  [Test, Category("RoundTrip")]
  public void Add_EmptyPayload_Allowed() {
    using var archive = BuildBaselineArchive(("a.bin", "AAA"));
    Pfs0InPlaceModifier.AddFiles(archive, [("empty.bin", Array.Empty<byte>())]);
    Assert.That(ReadEntry(archive, "empty.bin").Length, Is.EqualTo(0));
  }

  // ── Remove ──────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_NamedEntry_Disappears() {
    using var archive = BuildBaselineArchive(("a.bin", "AAA"), ("b.bin", "BBB"), ("c.bin", "CCC"));
    var removed = Pfs0InPlaceModifier.RemoveFiles(archive, ["b.bin"]);
    Assert.That(removed, Is.EqualTo(1));
    Assert.That(ListNames(archive), Is.EquivalentTo(new[] { "a.bin", "c.bin" }));
  }

  [Test, Category("RoundTrip")]
  public void Remove_PreservesOtherPayloads_ByteForByte() {
    using var archive = BuildBaselineArchive(("keep.bin", "KEEP-ME"), ("drop.bin", "DROP-ME"));
    var beforeKeep = ReadEntry(archive, "keep.bin");

    Pfs0InPlaceModifier.RemoveFiles(archive, ["drop.bin"]);
    var afterKeep = ReadEntry(archive, "keep.bin");

    Assert.That(afterKeep, Is.EqualTo(beforeKeep), "Surviving payload must be byte-identical after Remove.");
  }

  [Test, Category("RoundTrip")]
  public void Remove_WipesRemovedPayloadBytes() {
    using var archive = BuildBaselineArchive(
      ("secret.bin", "MARKER-XYZZY-PFS0-WIPE-CHECK"),
      ("other.bin", "harmless"));
    Pfs0InPlaceModifier.RemoveFiles(archive, ["secret.bin"]);
    var bytes = archive.ToArray();
    Assert.That(Encoding.UTF8.GetString(bytes), Does.Not.Contain("MARKER-XYZZY-PFS0-WIPE-CHECK"),
      "Removed payload bytes must not survive in the rewritten archive.");
  }

  [Test, Category("RoundTrip")]
  public void Remove_UnknownName_NoOp() {
    using var archive = BuildBaselineArchive(("a.bin", "AAA"));
    var removed = Pfs0InPlaceModifier.RemoveFiles(archive, ["does-not-exist.bin"]);
    Assert.That(removed, Is.EqualTo(0));
    Assert.That(ListNames(archive), Is.EquivalentTo(new[] { "a.bin" }));
  }

  // ── Descriptor interface routing ────────────────────────────────────────

  [Test, Category("Spec")]
  public void Descriptor_ImplementsModifiableInterface_AndAdvertisesRw() {
    var d = new Pfs0FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    // Add/Remove edit an existing PFS0 through Pfs0InPlaceModifier and return a
    // valid archive, which is the R/W contract FormatCapabilities.cs states —
    // the flag reports the public edit operation, not how many bytes the
    // relayout rewrites.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_ThenRemove_RoundTripsViaInterface() {
    var d = new Pfs0FormatDescriptor();
    using var archive = BuildBaselineArchive(("a.bin", "AAA"));

    ((IArchiveModifiable)d).Add(archive,
      [ArchiveInputInfo.InMemory("via-if.bin", Encoding.UTF8.GetBytes("via-if"))]);
    Assert.That(ListNames(archive), Does.Contain("via-if.bin"));

    ((IArchiveModifiable)d).Remove(archive, ["via-if.bin"]);
    Assert.That(ListNames(archive), Does.Not.Contain("via-if.bin"));
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_Roundtrip() {
    using var archive = BuildBaselineArchive(("k.bin", "kept"));
    Pfs0InPlaceModifier.AddFiles(archive, [("new1.bin", Encoding.UTF8.GetBytes("payload-1"))]);
    Pfs0InPlaceModifier.AddFiles(archive, [("new2.bin", Encoding.UTF8.GetBytes("payload-2"))]);
    Pfs0InPlaceModifier.RemoveFiles(archive, ["k.bin"]);

    Assert.That(ListNames(archive), Is.EquivalentTo(new[] { "new1.bin", "new2.bin" }));
    Assert.That(Encoding.UTF8.GetString(ReadEntry(archive, "new1.bin")), Is.EqualTo("payload-1"));
    Assert.That(Encoding.UTF8.GetString(ReadEntry(archive, "new2.bin")), Is.EqualTo("payload-2"));
  }
}
