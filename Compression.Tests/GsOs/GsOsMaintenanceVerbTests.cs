#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.GsOs;

namespace Compression.Tests.GsOs;

/// <summary>
/// Round-trip verification for the GS/OS 2IMG maintenance verbs. The descriptor
/// uses the interface-default <see cref="IArchiveDefragmentable"/> /
/// <see cref="IArchiveShrinkable"/> (verified extract → re-create rebuild): on a
/// ProDOS-ordered payload the inner file set and content must survive unchanged;
/// on a non-ProDOS payload the rebuild cannot be verified faithful, so defrag
/// must refuse (image byte-identical) and shrink must copy through unchanged.
/// </summary>
[TestFixture]
public class GsOsMaintenanceVerbTests {

  private static byte[] MakeBeta() {
    var b = new byte[1500];
    for (var i = 0; i < b.Length; i++) b[i] = (byte)(i * 13 + 5);
    return b;
  }

  private static readonly (string Name, byte[] Data)[] Seed = [
    ("ALPHA", Encoding.ASCII.GetBytes("alpha content for gsos maintenance")),
    ("BETA", MakeBeta()),
  ];

  private static byte[] MakeImage(GsOsFormatDescriptor d) {
    using var ms = new MemoryStream();
    d.Create(ms, [.. Seed.Select(s => ArchiveInputInfo.InMemory(s.Name, s.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(GsOsFormatDescriptor d, byte[] img) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb-gsosmaint-{Guid.NewGuid():N}");
    var map = new Dictionary<string, byte[]>();
    try {
      d.Extract(new MemoryStream(img), dir, null, null);
      foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        map[Path.GetFileName(f)] = File.ReadAllBytes(f);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
    return map;
  }

  /// <summary>Non-ProDOS-ordered 2IMG (image_format = 0 = DOS 3.3 order).</summary>
  private static byte[] BuildDos33Image() {
    var content = new byte[256];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)(i ^ 0x33);
    var img = new byte[64 + content.Length];
    Encoding.ASCII.GetBytes("2IMG").CopyTo(img.AsSpan(0, 4));
    Encoding.ASCII.GetBytes("XGS!").CopyTo(img.AsSpan(4, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(8, 2), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(10, 2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(12, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(16, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(20, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(24, 4), 64);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(28, 4), (uint)content.Length);
    content.CopyTo(img.AsSpan(64));
    return img;
  }

  [Test, Category("Spec")]
  public void Descriptor_ImplementsDefragAndShrinkMarkers() {
    var d = new GsOsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(d, Is.InstanceOf<IArchiveShrinkable>());
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesInnerProDosFiles() {
    var d = new GsOsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(MakeImage(d));
    ms.Position = 0;

    ((IArchiveDefragmentable)d).Defragment(ms);

    var img = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(img, 0, 4), Is.EqualTo("2IMG"), "stays a 2IMG container");
    var got = ReadAll(d, img);
    foreach (var (n, data) in Seed)
      Assert.That(got.GetValueOrDefault(n), Is.EqualTo(data), $"{n} must survive defrag byte-identically");
  }

  [Test, Category("RoundTrip")]
  public void Shrink_IsNonLossy_AndNeverGrows() {
    var d = new GsOsFormatDescriptor();
    var img = MakeImage(d);
    using var input = new MemoryStream(img);
    using var output = new MemoryStream();

    ((IArchiveShrinkable)d).Shrink(input, output);

    Assert.That(output.Length, Is.GreaterThan(0).And.LessThanOrEqualTo(img.Length),
      "shrink must never grow the image");
    var got = ReadAll(d, output.ToArray());
    foreach (var (n, data) in Seed)
      Assert.That(got.GetValueOrDefault(n), Is.EqualTo(data), $"{n} must survive shrink byte-identically");
  }

  [Test, Category("Spec")]
  public void NonProDosPayload_DefragRefuses_LeavingImageUntouched() {
    var d = new GsOsFormatDescriptor();
    var image = BuildDos33Image();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    // The opaque-blob surface cannot be re-created faithfully; the verified
    // rebuild must throw without committing anything.
    Assert.That(() => ((IArchiveDefragmentable)d).Defragment(ms), Throws.Exception);
    Assert.That(ms.ToArray(), Is.EqualTo(image), "refused defrag must leave the image byte-identical");
  }

  [Test, Category("Spec")]
  public void NonProDosPayload_ShrinkCopiesThroughUnchanged() {
    var d = new GsOsFormatDescriptor();
    var image = BuildDos33Image();
    using var input = new MemoryStream(image);
    using var output = new MemoryStream();

    ((IArchiveShrinkable)d).Shrink(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(image),
      "shrink must copy through unchanged when the rebuild can't be verified faithful");
  }
}
