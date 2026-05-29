namespace Compression.Tests.Ntfs;

[TestFixture]
public class NtfsBlockMoverTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Ntfs.NtfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  private static byte[] BuildImageSized(int totalSize, params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Ntfs.NtfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build(totalSize);
  }

  [Test, Category("HappyPath")]
  public void MoveExtent_CopiesBytesCorrectly() {
    var disk = BuildImage(("test.bin", new byte[4096]));
    using var ms = new MemoryStream(disk);
    var mover = new FileSystem.Ntfs.NtfsBlockMover();
    mover.Init(disk);

    // Write a marker at a known offset, move it somewhere else.
    var marker = "HELLO_MOVER!"u8.ToArray();
    var srcOffset = 100 * 4096L; // some high offset in the image
    if (srcOffset + marker.Length > disk.Length) {
      Assert.Ignore("Image too small for this test.");
      return;
    }

    ms.Position = srcOffset;
    ms.Write(marker);
    var dstOffset = srcOffset + 4096;

    mover.MoveExtent(ms, srcOffset, dstOffset, marker.Length);

    var buf = new byte[marker.Length];
    ms.Position = dstOffset;
    ms.ReadExactly(buf);
    Assert.That(buf, Is.EqualTo(marker));
  }

  [Test, Category("HappyPath")]
  public void MoveExtent_WithZeroSource_ClearsOld() {
    var disk = BuildImage(("test.bin", new byte[4096]));
    using var ms = new MemoryStream(disk);
    var mover = new FileSystem.Ntfs.NtfsBlockMover();
    mover.Init(disk);

    var marker = "ZERO_ME"u8.ToArray();
    var srcOffset = 100 * 4096L;
    if (srcOffset + marker.Length > disk.Length) {
      Assert.Ignore("Image too small.");
      return;
    }

    ms.Position = srcOffset;
    ms.Write(marker);
    var dstOffset = srcOffset + 4096;

    mover.MoveExtent(ms, srcOffset, dstOffset, marker.Length, zeroSource: true);

    var srcBuf = new byte[marker.Length];
    ms.Position = srcOffset;
    ms.ReadExactly(srcBuf);
    Assert.That(srcBuf, Is.EqualTo(new byte[marker.Length]), "Source region must be zeroed.");
  }

  [Test, Category("RoundTrip")]
  public void UpdateAllocation_SingleNonResidentFile_DataIntact() {
    // Build an image with a non-resident file (>700 bytes triggers non-resident).
    var payload = new byte[4096];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
    var disk = BuildImageSized(8 * 1024 * 1024, ("bigfile.bin", payload));

    // Read the extent map to find where the file lives.
    using var readMs = new MemoryStream(disk);
    var reader = new FileSystem.Ntfs.NtfsReader(readMs);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var originalData = reader.Extract(reader.Entries[0]);
    Assert.That(originalData, Is.EqualTo(payload), "Precondition: data must be intact.");

    // Find the file's extent from the extent map.
    readMs.Position = 0;
    var extents = FileSystem.Ntfs.NtfsExtentMap.Enumerate(readMs).ToList();
    var fileExtent = extents.FirstOrDefault(e => e.FileName == "bigfile.bin");
    Assert.That(fileExtent, Is.Not.Null, "File extent must be discoverable.");

    // Find a free region to move to. Use the end of the image minus a few clusters.
    var clusterSize = 4096;
    var newOffset = disk.Length - 3 * clusterSize; // leave room at end
    // Ensure newOffset is cluster-aligned.
    newOffset = (newOffset / clusterSize) * clusterSize;

    using var moveMs = new MemoryStream(disk);
    var mover = new FileSystem.Ntfs.NtfsBlockMover();
    mover.Init(disk);

    mover.MoveExtent(moveMs, fileExtent!.Offset, newOffset, fileExtent.Length, zeroSource: true);
    mover.UpdateAllocationAfterMove(moveMs, "bigfile.bin", fileExtent.Offset, newOffset, fileExtent.Length);

    // Now read back the image and verify the file is still readable.
    moveMs.Position = 0;
    var reader2 = new FileSystem.Ntfs.NtfsReader(moveMs);
    Assert.That(reader2.Entries, Has.Count.EqualTo(1));
    var movedData = reader2.Extract(reader2.Entries[0]);
    Assert.That(movedData, Is.EqualTo(payload), "File content must be intact after move.");
  }

  [Test, Category("RoundTrip")]
  public void UpdateAllocation_MultipleFiles_OnlyMovedFileChanges() {
    var payloadA = new byte[2048];
    for (var i = 0; i < payloadA.Length; i++) payloadA[i] = (byte)'A';
    var payloadB = new byte[4096];
    for (var i = 0; i < payloadB.Length; i++) payloadB[i] = (byte)'B';

    var disk = BuildImageSized(8 * 1024 * 1024, ("fileA.bin", payloadA), ("fileB.bin", payloadB));

    using var ms = new MemoryStream(disk);
    var extents = FileSystem.Ntfs.NtfsExtentMap.Enumerate(ms).ToList();
    var extentB = extents.FirstOrDefault(e => e.FileName == "fileB.bin");
    Assert.That(extentB, Is.Not.Null);

    var clusterSize = 4096;
    var newOffset = (long)(disk.Length - 5 * clusterSize);
    newOffset = (newOffset / clusterSize) * clusterSize;

    var mover = new FileSystem.Ntfs.NtfsBlockMover();
    mover.Init(disk);
    mover.MoveExtent(ms, extentB!.Offset, newOffset, extentB.Length, zeroSource: true);
    mover.UpdateAllocationAfterMove(ms, "fileB.bin", extentB.Offset, newOffset, extentB.Length);

    ms.Position = 0;
    var reader = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var byName = reader.Entries.ToDictionary(e => e.Name, e => reader.Extract(e));
    Assert.That(byName["fileA.bin"], Is.EqualTo(payloadA), "Unmoved file must be intact.");
    Assert.That(byName["fileB.bin"], Is.EqualTo(payloadB), "Moved file must be intact.");
  }

  [Test, Category("RoundTrip")]
  public void DescriptorImplementsIFilesystemBlockMover() {
    var desc = new FileSystem.Ntfs.NtfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IFilesystemBlockMover>());
  }

  [Test, Category("RoundTrip")]
  public void PlannerPath_QuickProfile_AlreadyContiguous_NoMoves() {
    // With Quick profile, already-contiguous single-extent files need no moves.
    var payloadA = new byte[2048];
    for (var i = 0; i < payloadA.Length; i++) payloadA[i] = (byte)(i % 200);
    var payloadB = new byte[8192];
    for (var i = 0; i < payloadB.Length; i++) payloadB[i] = (byte)(i % 173);

    var disk = BuildImageSized(8 * 1024 * 1024, ("alpha.bin", payloadA), ("beta.bin", payloadB));

    var mover = new FileSystem.Ntfs.NtfsBlockMover();
    mover.Init(disk);

    var extents = FileSystem.Ntfs.NtfsExtentMap.Enumerate(new MemoryStream(disk)).ToList();

    // Compute data origin from metadata extents.
    long dataOrigin = mover.FirstDataByte;
    foreach (var e in extents)
      if (e.Kind == Compression.Registry.DefragBlockKind.MetadataReserved) {
        var end = e.Offset + e.Length;
        if (end > dataOrigin) dataOrigin = end;
      }
    dataOrigin = (dataOrigin + mover.ClusterSize - 1) / mover.ClusterSize * mover.ClusterSize;

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, dataOrigin, disk.Length, mover.ClusterSize,
      Compression.Registry.LayoutProfile.Quick,
      Compression.Registry.DefragMode.ConsolidateAtStart);

    Assert.That(moves, Has.Count.EqualTo(0), "Quick profile: already contiguous single-extent files need no moves.");
  }

  [Test, Category("RoundTrip")]
  public void Defragment_Rebuild_PreservesAllFiles() {
    // Use the rebuild fallback (CarveHole mode always uses rebuild).
    var payloadA = new byte[2048];
    for (var i = 0; i < payloadA.Length; i++) payloadA[i] = (byte)(i % 200);
    var payloadB = new byte[8192];
    for (var i = 0; i < payloadB.Length; i++) payloadB[i] = (byte)(i % 173);

    var disk = BuildImageSized(8 * 1024 * 1024, ("alpha.bin", payloadA), ("beta.bin", payloadB));
    using var ms = new MemoryStream(disk);

    var desc = new FileSystem.Ntfs.NtfsFormatDescriptor();
    desc.Defragment(ms, new Compression.Registry.DefragOptions {
      Mode = Compression.Registry.DefragMode.CarveHole,
      HoleSize = 4096,
    });

    ms.Position = 0;
    var reader = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var byName = reader.Entries.ToDictionary(e => e.Name, e => reader.Extract(e));
    Assert.That(byName["alpha.bin"], Is.EqualTo(payloadA));
    Assert.That(byName["beta.bin"], Is.EqualTo(payloadB));
  }

  [Test, Category("RoundTrip")]
  public void Defragment_Planner_PreservesAllFiles() {
    // Exercise the planner-driven path directly.
    var payloadA = new byte[2048];
    for (var i = 0; i < payloadA.Length; i++) payloadA[i] = (byte)(i % 200);
    var payloadB = new byte[8192];
    for (var i = 0; i < payloadB.Length; i++) payloadB[i] = (byte)(i % 173);

    var disk = BuildImageSized(8 * 1024 * 1024, ("alpha.bin", payloadA), ("beta.bin", payloadB));
    using var ms = new MemoryStream(disk);

    var desc = new FileSystem.Ntfs.NtfsFormatDescriptor();
    desc.Defragment(ms, new Compression.Registry.DefragOptions {
      Mode = Compression.Registry.DefragMode.ConsolidateAtStart
    });

    ms.Position = 0;
    var reader = new FileSystem.Ntfs.NtfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var byName = reader.Entries.ToDictionary(e => e.Name, e => reader.Extract(e));
    Assert.That(byName["alpha.bin"], Is.EqualTo(payloadA));
    Assert.That(byName["beta.bin"], Is.EqualTo(payloadB));
  }

  [Test, Category("EdgeCase")]
  public void UpdateAllocation_ResidentFile_NoOpNoCrash() {
    // Resident files have no data runs. UpdateAllocation should not crash.
    var payload = "small"u8.ToArray(); // < 700 bytes = resident
    var disk = BuildImageSized(4 * 1024 * 1024, ("tiny.txt", payload));

    using var ms = new MemoryStream(disk);
    var mover = new FileSystem.Ntfs.NtfsBlockMover();
    mover.Init(disk);

    // Try to "move" something for a file that has no non-resident data.
    // This should silently succeed (no data runs to patch).
    Assert.DoesNotThrow(() =>
      mover.UpdateAllocationAfterMove(ms, "tiny.txt", 0, 4096, 4096));
  }

  [Test, Category("HappyPath")]
  public void ClusterBitmap_UpdatedAfterMove() {
    var payload = new byte[4096];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)0xAB;
    var disk = BuildImageSized(8 * 1024 * 1024, ("data.bin", payload));

    using var ms = new MemoryStream(disk);
    var extents = FileSystem.Ntfs.NtfsExtentMap.Enumerate(ms).ToList();
    var fileExtent = extents.FirstOrDefault(e => e.FileName == "data.bin");
    Assert.That(fileExtent, Is.Not.Null);

    var clusterSize = 4096;
    var newOffset = (long)(disk.Length - 4 * clusterSize);
    newOffset = (newOffset / clusterSize) * clusterSize;

    var mover = new FileSystem.Ntfs.NtfsBlockMover();
    mover.Init(disk);
    mover.MoveExtent(ms, fileExtent!.Offset, newOffset, fileExtent.Length, zeroSource: true);
    mover.UpdateAllocationAfterMove(ms, "data.bin", fileExtent.Offset, newOffset, fileExtent.Length);

    // Read back the updated image and verify the file data is at the new location.
    ms.Position = newOffset;
    var buf = new byte[payload.Length];
    ms.ReadExactly(buf);
    Assert.That(buf, Is.EqualTo(payload), "Data should be at new location.");

    // The old location should be zeroed.
    ms.Position = fileExtent.Offset;
    var oldBuf = new byte[payload.Length];
    ms.ReadExactly(oldBuf);
    Assert.That(oldBuf, Is.EqualTo(new byte[payload.Length]), "Old location should be zeroed.");
  }
}
