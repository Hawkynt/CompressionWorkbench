using Compression.Core.DiskImage.Raid;

namespace Compression.Tests.Raid;

/// <summary>
/// Pure-managed proofs of the RAID address mapping and parity reconstruction, with no
/// external tools. RAID0/Linear/RAID1 layouts are asserted directly; RAID5 uses an
/// independent textbook left-symmetric encoder so that production decode (and its XOR
/// reconstruction) is cross-checked against a second implementation. The mdadm
/// end-to-end fixture proves the same layouts against the real Linux driver.
/// </summary>
[TestFixture]
public class RaidAssembledStreamTests {
  private const int Chunk = 512;

  // ── RAID0 ─────────────────────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Raid0_TwoMembers_RoundRobinsChunks() {
    // member0 holds even logical chunks, member1 holds odd ones.
    var m0 = new byte[3 * Chunk];
    var m1 = new byte[3 * Chunk];
    FillChunk(m0, 0, 0); // logical chunk 0
    FillChunk(m1, 0, 1); // logical chunk 1
    FillChunk(m0, 1, 2); // logical chunk 2
    FillChunk(m1, 1, 3); // logical chunk 3
    FillChunk(m0, 2, 4); // logical chunk 4
    FillChunk(m1, 2, 5); // logical chunk 5

    var array = BuildArray(RaidLevel.Raid0, raidDisks: 2, perDevice: m0.LongLength,
      new[] { m0, m1 });
    using var s = new RaidAssembledStream(array);

    var got = ReadAll(s);
    Assert.That(got.Length, Is.EqualTo(6 * Chunk));
    for (var vc = 0; vc < 6; vc++)
      AssertChunk(got, vc, vc);
  }

  // ── Linear ────────────────────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Linear_ConcatenatesMembersInRoleOrder() {
    var m0 = new byte[2 * Chunk];
    var m1 = new byte[2 * Chunk];
    FillChunk(m0, 0, 10); FillChunk(m0, 1, 11);
    FillChunk(m1, 0, 12); FillChunk(m1, 1, 13);

    var array = BuildArray(RaidLevel.Linear, raidDisks: 2, perDevice: m0.LongLength,
      new[] { m0, m1 });
    using var s = new RaidAssembledStream(array);

    var got = ReadAll(s);
    Assert.That(got.Length, Is.EqualTo(4 * Chunk));
    AssertChunk(got, 0, 10); AssertChunk(got, 1, 11);
    AssertChunk(got, 2, 12); AssertChunk(got, 3, 13);
  }

  // ── RAID1 ─────────────────────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Raid1_ReadsFirstMirror_AndSurvivesMissingFirst() {
    var m0 = new byte[2 * Chunk];
    FillChunk(m0, 0, 20); FillChunk(m0, 1, 21);
    var m1 = (byte[])m0.Clone();

    var full = BuildArray(RaidLevel.Raid1, raidDisks: 2, perDevice: m0.LongLength,
      new[] { m0, m1 });
    using (var s = new RaidAssembledStream(full)) {
      var got = ReadAll(s);
      AssertChunk(got, 0, 20); AssertChunk(got, 1, 21);
    }

    // Drop role 0 -> must read from the surviving mirror.
    var degraded = BuildArray(RaidLevel.Raid1, raidDisks: 2, perDevice: m0.LongLength,
      new[] { (byte[]?)null, m1 });
    using (var s = new RaidAssembledStream(degraded)) {
      var got = ReadAll(s);
      AssertChunk(got, 0, 20); AssertChunk(got, 1, 21);
    }
  }

  // ── RAID5 ─────────────────────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Raid5_LeftSymmetric_DecodesAndReconstructsAnyMissingMember() {
    const int disks = 3;
    const int logicalChunks = 8;
    var logical = new byte[logicalChunks * Chunk];
    for (var vc = 0; vc < logicalChunks; vc++)
      FillChunk(logical, vc, 100 + vc);

    var members = EncodeRaid5LeftSymmetric(logical, disks, Chunk);
    var perDevice = members[0].LongLength;

    // Non-degraded decode reproduces the logical byte stream.
    var full = BuildArray(RaidLevel.Raid5, raidDisks: disks, perDevice: perDevice, members,
      layout: 2, chunk: Chunk);
    using (var s = new RaidAssembledStream(full)) {
      var got = ReadAll(s);
      Assert.That(got.AsSpan(0, logical.Length).ToArray(), Is.EqualTo(logical),
        "non-degraded RAID5 decode must reproduce the logical stream");
    }

    // Dropping any single member still reconstructs the logical stream via XOR.
    for (var missing = 0; missing < disks; missing++) {
      var degradedMembers = new byte[]?[disks];
      for (var i = 0; i < disks; i++) degradedMembers[i] = i == missing ? null : members[i];
      var degraded = BuildArray(RaidLevel.Raid5, raidDisks: disks, perDevice: perDevice,
        degradedMembers, layout: 2, chunk: Chunk);
      using var s = new RaidAssembledStream(degraded);
      var got = ReadAll(s);
      Assert.That(got.AsSpan(0, logical.Length).ToArray(), Is.EqualTo(logical),
        $"RAID5 must reconstruct the logical stream with member {missing} missing");
    }
  }

  [Test, Category("EdgeCase")]
  public void Raid5_TwoMissingMembers_Throws() {
    const int disks = 3;
    var logical = new byte[6 * Chunk];
    for (var vc = 0; vc < 6; vc++) FillChunk(logical, vc, 50 + vc);
    var members = EncodeRaid5LeftSymmetric(logical, disks, Chunk);

    var m = new byte[]?[disks];
    m[0] = null; m[1] = null; m[2] = members[2];
    var degraded = BuildArray(RaidLevel.Raid5, raidDisks: disks, perDevice: members[0].LongLength,
      m, layout: 2, chunk: Chunk);
    using var s = new RaidAssembledStream(degraded);
    Assert.Throws<InvalidOperationException>(() => ReadAll(s));
  }

  // ── independent left-symmetric encoder (test oracle) ────────────────────
  /// <summary>
  /// Textbook RAID5 left-symmetric encoder used purely to produce self-consistent member
  /// images (data + XOR parity) for the decode/reconstruct assertions above. Kept
  /// independent of the production mapping so the two cross-check each other.
  /// </summary>
  private static byte[][] EncodeRaid5LeftSymmetric(byte[] logical, int disks, int chunk) {
    var dataDisks = disks - 1;
    var logicalChunks = logical.Length / chunk;
    var stripes = (logicalChunks + dataDisks - 1) / dataDisks;
    var members = new byte[disks][];
    for (var i = 0; i < disks; i++) members[i] = new byte[stripes * chunk];

    for (var vc = 0; vc < logicalChunks; vc++) {
      var idxInStripe = vc % dataDisks;
      var stripe = vc / dataDisks;
      var pd = dataDisks - (stripe % disks);
      var dd = (pd + 1 + idxInStripe) % disks;
      Array.Copy(logical, vc * chunk, members[dd], stripe * chunk, chunk);
    }
    // Parity per stripe = XOR of the data-disk chunks on that stripe.
    for (var stripe = 0; stripe < stripes; stripe++) {
      var pd = dataDisks - (stripe % disks);
      for (var b = 0; b < chunk; b++) {
        byte x = 0;
        for (var disk = 0; disk < disks; disk++)
          if (disk != pd) x ^= members[disk][stripe * chunk + b];
        members[pd][stripe * chunk + b] = x;
      }
    }
    return members;
  }

  // ── helpers ─────────────────────────────────────────────────────────────
  private static RaidArray BuildArray(RaidLevel level, int raidDisks, long perDevice,
      IReadOnlyList<byte[]?> memberData, int layout = 0, long chunk = Chunk) {
    var members = new List<RaidMember>(raidDisks);
    for (var role = 0; role < raidDisks; role++) {
      var data = memberData[role];
      members.Add(new RaidMember {
        Role = role,
        Data = data == null ? null : new MemoryStream(data, writable: false),
        DataOffsetBytes = 0,
        DataSizeBytes = perDevice,
      });
    }
    return new RaidArray {
      Level = level,
      ChunkSizeBytes = chunk,
      RaidDisks = raidDisks,
      Layout = layout,
      Members = members,
      PerDeviceDataBytes = perDevice,
    };
  }

  private static void FillChunk(byte[] buffer, int chunkIndex, int tag) {
    var start = chunkIndex * Chunk;
    for (var i = 0; i < Chunk; i++)
      buffer[start + i] = (byte)((tag * 31 + i * 7) & 0xFF);
  }

  private static void AssertChunk(byte[] buffer, int chunkIndex, int tag) {
    var start = chunkIndex * Chunk;
    for (var i = 0; i < Chunk; i++)
      Assert.That(buffer[start + i], Is.EqualTo((byte)((tag * 31 + i * 7) & 0xFF)),
        $"chunk {chunkIndex} byte {i} (expected tag {tag})");
  }

  private static byte[] ReadAll(Stream s) {
    s.Position = 0;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
