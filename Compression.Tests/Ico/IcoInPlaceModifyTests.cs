using System.Buffers.Binary;
using Compression.Registry;
using CompressionWorkbench.FileFormat.Ico;

namespace Compression.Tests.Ico;

/// <summary>
/// In-place R/W coverage for the Windows ICO bundle: <see cref="IcoInPlaceModifier"/>
/// against the existing ICONDIR + ICONDIRENTRY directory and the per-image
/// payloads packed after it. The companion WORM tests live in <see cref="IcoTests"/>.
///
/// Boundaries exercised:
/// <list type="bullet">
///   <item>Add appends payload at EOF; existing payload bytes preserved verbatim</item>
///   <item>Add patches every existing dir-entry offset by +16</item>
///   <item>Reader round-trip after Add picks up the new image</item>
///   <item>Remove collapses both the 16-byte dir slot and the payload bytes</item>
///   <item>Remove patches every surviving dir-entry offset correctly</item>
///   <item>Removed payload bytes are physically wiped — no forensic trace</item>
///   <item>Sequence: Add then Remove returns to the original entry list</item>
/// </list>
/// </summary>
[TestFixture]
public class IcoInPlaceModifyTests {

  // ── Fixtures (mirrored from IcoTests so the two test classes don't entangle) ──

  private static byte[] MinimalPng(int width, int height) {
    static byte[] Chunk(string type, byte[] data) {
      var buf = new byte[4 + 4 + data.Length + 4];
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)data.Length);
      for (var i = 0; i < 4; i++) buf[4 + i] = (byte)type[i];
      data.CopyTo(buf.AsSpan(8));
      return buf;
    }

    var ihdr = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
    ihdr[8] = 8; ihdr[9] = 6;

    var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    using var ms = new MemoryStream();
    ms.Write(sig);
    ms.Write(Chunk("IHDR", ihdr));
    ms.Write(Chunk("IDAT", [0x78, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]));
    ms.Write(Chunk("IEND", []));
    return ms.ToArray();
  }

  /// <summary>Two-image baseline image: 16×16 + 32×32 PNGs.</summary>
  private static MemoryStream FreshBundle() {
    var ms = new MemoryStream();
    var ico = IcoWriter.BuildIco([
      new IcoWriter.Image(MinimalPng(16, 16)),
      new IcoWriter.Image(MinimalPng(32, 32)),
    ]);
    ms.Write(ico);
    ms.Position = 0;
    // Expandable MemoryStream so SetLength() works for grow/shrink paths.
    return ms;
  }

  private static int Count(byte[] blob) =>
    BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(4, 2));

  private static (uint Size, uint Off) DirEntry(byte[] blob, int index) {
    var off = 6 + 16 * index;
    return (
      BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 8, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 12, 4)));
  }

  // ── Add ──────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_NewImage_AppearsInList() {
    using var image = FreshBundle();
    var preNames = IcoReader.Read(image.ToArray()).Entries.Select(e => e.Name).ToList();

    var addedPng = MinimalPng(64, 64);
    IcoInPlaceModifier.AddImage(image, addedPng);

    var bundle = IcoReader.Read(image.ToArray());
    Assert.That(bundle.Entries, Has.Count.EqualTo(preNames.Count + 1));
    Assert.That(bundle.Entries[^1].Width, Is.EqualTo(64));
    Assert.That(bundle.Entries[^1].Height, Is.EqualTo(64));
    Assert.That(bundle.Entries[^1].IsPng, Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Add_ExistingPayloadBytes_PreservedByteIdentical() {
    using var image = FreshBundle();
    var preBlob = image.ToArray();
    var pre = IcoReader.Read(preBlob).Entries.Select(e => e.Data).ToList();

    IcoInPlaceModifier.AddImage(image, MinimalPng(48, 48));

    var post = IcoReader.Read(image.ToArray()).Entries.Select(e => e.Data).ToList();
    // The first 'pre.Count' entries' decoded bytes must equal the pre-add bytes
    // exactly — only the trailing entry is new.
    for (var i = 0; i < pre.Count; i++)
      Assert.That(post[i], Is.EqualTo(pre[i]).AsCollection, $"entry {i} bytes drifted");
  }

  [Test, Category("HappyPath")]
  public void Add_PatchesExistingDirEntryOffsetsBy16() {
    using var image = FreshBundle();
    var preBlob = image.ToArray();
    var preCount = Count(preBlob);
    var preEntries = new List<(uint Size, uint Off)>();
    for (var i = 0; i < preCount; i++) preEntries.Add(DirEntry(preBlob, i));

    IcoInPlaceModifier.AddImage(image, MinimalPng(48, 48));

    var postBlob = image.ToArray();
    Assert.That(Count(postBlob), Is.EqualTo(preCount + 1));
    for (var i = 0; i < preCount; i++) {
      var (size, off) = DirEntry(postBlob, i);
      Assert.That(size, Is.EqualTo(preEntries[i].Size), $"size of entry {i} drifted");
      Assert.That(off, Is.EqualTo(preEntries[i].Off + 16),
        $"offset of entry {i} not patched by exactly +16");
    }
  }

  [Test, Category("Boundary")]
  public void Add_BmpInput_ProducesValidDibEntry() {
    using var image = FreshBundle();
    var bmp = MinimalBmp(16, 16);
    IcoInPlaceModifier.AddImage(image, bmp);

    var bundle = IcoReader.Read(image.ToArray());
    Assert.That(bundle.Entries[^1].IsPng, Is.False);
    Assert.That(bundle.Entries[^1].Width, Is.EqualTo(16));
    Assert.That(bundle.Entries[^1].Height, Is.EqualTo(16));
  }

  [Test, Category("Exceptional")]
  public void Add_InvalidImageBytes_Throws() {
    using var image = FreshBundle();
    Assert.That(
      () => IcoInPlaceModifier.AddImage(image, [0xFF, 0xFF, 0xFF, 0xFF]),
      Throws.ArgumentException);
  }

  // ── Remove ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_NamedEntry_DisappearsFromList() {
    using var image = FreshBundle();
    var pre = IcoReader.Read(image.ToArray());
    Assert.That(pre.Entries, Has.Count.EqualTo(2));
    var firstName = pre.Entries[0].Name;
    var survivorBytes = pre.Entries[1].Data;

    IcoInPlaceModifier.RemoveImage(image, firstName);

    var post = IcoReader.Read(image.ToArray());
    Assert.That(post.Entries, Has.Count.EqualTo(1));
    // Surviving payload bytes preserved verbatim — only the dir-slot offsets shifted.
    Assert.That(post.Entries[0].Data, Is.EqualTo(survivorBytes).AsCollection);
  }

  [Test, Category("Boundary")]
  public void Remove_LastEntry_LeavesValidSingleEntryBundle() {
    using var image = FreshBundle();
    var pre = IcoReader.Read(image.ToArray());
    var lastName = pre.Entries[^1].Name;
    var survivorBytes = pre.Entries[0].Data;

    IcoInPlaceModifier.RemoveImage(image, lastName);

    var post = IcoReader.Read(image.ToArray());
    Assert.That(post.Entries, Has.Count.EqualTo(1));
    Assert.That(post.Entries[0].Data, Is.EqualTo(survivorBytes).AsCollection);
  }

  [Test, Category("Exceptional")]
  public void Remove_UnknownName_ThrowsFileNotFound() {
    using var image = FreshBundle();
    Assert.That(
      () => IcoInPlaceModifier.RemoveImage(image, "nope.png"),
      Throws.InstanceOf<FileNotFoundException>());
  }

  [Test, Category("RoundTrip")]
  public void RemovedPayload_BytesWipedFromImage() {
    using var image = FreshBundle();
    var pre = IcoReader.Read(image.ToArray());
    var removedBytes = pre.Entries[0].Data;
    var removedName = pre.Entries[0].Name;

    IcoInPlaceModifier.RemoveImage(image, removedName);

    // The removed PNG signature (89 50 4E 47 …) must no longer appear inside
    // the bundle bytes (the surviving entry is a different-size PNG, so its
    // signature is at a different byte offset).
    var post = image.ToArray();
    Assert.That(post.Length, Is.LessThan(pre.Entries[0].Data.Length + 16 + 22)); // sanity
    // Stronger: count the number of PNG signatures in the post-blob — must
    // equal the surviving entry count (one).
    var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    var sigCount = 0;
    for (var i = 0; i + sig.Length <= post.Length; i++) {
      if (post.AsSpan(i, sig.Length).SequenceEqual(sig)) sigCount++;
    }
    Assert.That(sigCount, Is.EqualTo(1),
      "removed PNG payload bytes should not survive anywhere in the post-blob");
    // And the removed bytes themselves shouldn't appear as a contiguous run.
    var found = false;
    for (var i = 0; i + removedBytes.Length <= post.Length && !found; i++) {
      if (post.AsSpan(i, removedBytes.Length).SequenceEqual(removedBytes)) found = true;
    }
    Assert.That(found, Is.False, "removed payload bytes survive as a contiguous run");
  }

  // ── Sequence ─────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddThenRemove_RestoresEntryCountAndSurvivorBytes() {
    using var image = FreshBundle();
    var preBlob = image.ToArray();
    var preBundle = IcoReader.Read(preBlob);

    IcoInPlaceModifier.AddImage(image, MinimalPng(48, 48));
    var afterAdd = IcoReader.Read(image.ToArray());
    Assert.That(afterAdd.Entries, Has.Count.EqualTo(preBundle.Entries.Count + 1));

    // Remove the just-added entry by name.
    IcoInPlaceModifier.RemoveImage(image, afterAdd.Entries[^1].Name);

    var afterRemove = IcoReader.Read(image.ToArray());
    Assert.That(afterRemove.Entries, Has.Count.EqualTo(preBundle.Entries.Count));
    for (var i = 0; i < preBundle.Entries.Count; i++)
      Assert.That(afterRemove.Entries[i].Data, Is.EqualTo(preBundle.Entries[i].Data).AsCollection);
  }

  // ── Descriptor wiring ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var desc = new IcoFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AddRemove_RoutesThroughInPlaceModifier() {
    using var image = FreshBundle();
    var desc = (IArchiveModifiable)new IcoFormatDescriptor();

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, MinimalPng(40, 40));
      desc.Add(image, [new ArchiveInputInfo(tmp, Path.GetFileName(tmp), false)]);
      var bundle = IcoReader.Read(image.ToArray());
      Assert.That(bundle.Entries, Has.Count.EqualTo(3));

      desc.Remove(image, [bundle.Entries[^1].Name]);
      var after = IcoReader.Read(image.ToArray());
      Assert.That(after.Entries, Has.Count.EqualTo(2));
    } finally {
      if (File.Exists(tmp)) File.Delete(tmp);
    }
  }

  // ── BMP fixture ──────────────────────────────────────────────────────────

  private static byte[] MinimalBmp(int width, int height) {
    const int fileHeader = 14, infoHeader = 40;
    var rowBytes = ((width * 32 + 31) / 32) * 4;
    var pixelBytes = rowBytes * height;
    var fileLen = fileHeader + infoHeader + pixelBytes;
    var data = new byte[fileLen];
    data[0] = (byte)'B'; data[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2, 4), (uint)fileLen);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10, 4), (uint)(fileHeader + infoHeader));
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14, 4), infoHeader);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18, 4), width);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22, 4), height);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28, 2), 32);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(30, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34, 4), (uint)pixelBytes);
    for (var i = 0; i < pixelBytes; i++) data[fileHeader + infoHeader + i] = 0xFF;
    return data;
  }
}
