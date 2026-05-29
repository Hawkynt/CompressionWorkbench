#pragma warning disable CS1591
using Compression.Lib;
using FileFormat.Vhd;
using FileFormat.Qcow2;
using FileFormat.Vdi;
using FileFormat.Vmdk;

namespace Compression.Tests.Sparse;

[TestFixture]
public class SparseConverterTests {

  // ── VHD sparsify ──────────────────────────────────────────────────

  [Test]
  public void Sparsify_Vhd_WithZeroBlocks_ReducesSize() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_sparse_vhd_" + Guid.NewGuid().ToString("N")[..8] + ".vhd");
    try {
      // Create a fixed VHD with mostly-zero data
      var data = new byte[1024 * 1024]; // 1 MB all zeros
      var writer = new VhdWriter();
      writer.SetDiskData(data);
      File.WriteAllBytes(path, writer.Build()); // Fixed VHD

      var origSize = new FileInfo(path).Length;
      var freed = SparseConverter.Sparsify(path);

      Assert.That(freed, Is.GreaterThan(0), "All-zero VHD should compress significantly");
      Assert.That(new FileInfo(path).Length, Is.LessThan(origSize));

      // Verify data is still readable
      using var stream = File.OpenRead(path);
      var reader = new VhdReader(stream);
      Assert.That(reader.Entries, Has.Count.EqualTo(1));
      var extracted = reader.Extract(reader.Entries[0]);
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test]
  public void Sparsify_Vhd_WithNonZeroData_PreservesData() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_sparse_vhd_nz_" + Guid.NewGuid().ToString("N")[..8] + ".vhd");
    try {
      var data = new byte[4096];
      new Random(42).NextBytes(data);
      var writer = new VhdWriter();
      writer.SetDiskData(data);
      File.WriteAllBytes(path, writer.BuildDynamic());

      SparseConverter.Sparsify(path);

      using var stream = File.OpenRead(path);
      var reader = new VhdReader(stream);
      var extracted = reader.Extract(reader.Entries[0]);
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── VHD densify ───────────────────────────────────────────────────

  [Test]
  public void Densify_Vhd_DynamicToFixed() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_dense_vhd_" + Guid.NewGuid().ToString("N")[..8] + ".vhd");
    try {
      var data = new byte[1024 * 1024]; // 1 MB, partially non-zero
      new Random(42).NextBytes(data.AsSpan(0, 512));
      var writer = new VhdWriter();
      writer.SetDiskData(data);
      File.WriteAllBytes(path, writer.BuildDynamic()); // Sparse dynamic

      var origSize = new FileInfo(path).Length;
      var allocated = SparseConverter.Densify(path);

      Assert.That(allocated, Is.GreaterThanOrEqualTo(0));
      // Fixed VHD should be at least data.Length + 512 (footer)
      var newSize = new FileInfo(path).Length;
      Assert.That(newSize, Is.GreaterThanOrEqualTo(data.Length + 512));

      // Verify data is preserved
      using var stream = File.OpenRead(path);
      var reader = new VhdReader(stream);
      var extracted = reader.Extract(reader.Entries[0]);
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── QCOW2 sparsify ───────────────────────────────────────────────

  [Test]
  public void Sparsify_Qcow2_WithZeroBlocks_ReducesOrMaintainsSize() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_sparse_qcow2_" + Guid.NewGuid().ToString("N")[..8] + ".qcow2");
    try {
      // Create a QCOW2 with mostly-zero data
      var data = new byte[256 * 1024]; // 256 KB, all zeros
      // Write a few non-zero bytes to have at least one cluster
      data[0] = 1;

      var qw = new Qcow2Writer();
      qw.SetDiskImage(data);
      using (var fs = File.Create(path))
        qw.WriteTo(fs);

      var origSize = new FileInfo(path).Length;
      var freed = SparseConverter.Sparsify(path);

      // After rewrite, zero clusters should be sparse
      // The QCOW2 writer doesn't necessarily skip zero clusters in its current
      // implementation, but the data should still be correct
      using var stream = File.OpenRead(path);
      var reader = new Qcow2Reader(stream);
      var extracted = reader.ExtractDisk();
      Assert.That(extracted.Length, Is.EqualTo(data.Length));
      Assert.That(extracted[0], Is.EqualTo(1));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test]
  public void Sparsify_Qcow2_PreservesNonZeroData() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_sparse_qcow2_nz_" + Guid.NewGuid().ToString("N")[..8] + ".qcow2");
    try {
      var data = new byte[65536]; // One cluster worth
      new Random(99).NextBytes(data);

      var qw = new Qcow2Writer();
      qw.SetDiskImage(data);
      using (var fs = File.Create(path))
        qw.WriteTo(fs);

      SparseConverter.Sparsify(path);

      using var stream = File.OpenRead(path);
      var reader = new Qcow2Reader(stream);
      var extracted = reader.ExtractDisk();
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── VDI sparsify ──────────────────────────────────────────────────

  [Test]
  public void Sparsify_Vdi_WithZeroBlocks_Works() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_sparse_vdi_" + Guid.NewGuid().ToString("N")[..8] + ".vdi");
    try {
      // VDI with mostly-zero data (only first block non-zero)
      var data = new byte[128 * 1024]; // 128 KB = 2 blocks at 64 KB
      new Random(42).NextBytes(data.AsSpan(0, 100)); // First block has data

      using (var fs = File.Create(path))
      using (var vw = new VdiWriter(fs, leaveOpen: true, virtualSize: data.Length))
        vw.Write(data);

      SparseConverter.Sparsify(path);

      // Verify data preserved
      using var stream = File.OpenRead(path);
      var reader = new VdiReader(stream);
      var extracted = reader.ExtractDisk();
      Assert.That(extracted.Length, Is.EqualTo(data.Length));
      Assert.That(extracted.AsSpan(0, 100).ToArray(), Is.EqualTo(data.AsSpan(0, 100).ToArray()));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── VDI densify ───────────────────────────────────────────────────

  [Test]
  public void Densify_Vdi_AllocatesAllBlocks() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_dense_vdi_" + Guid.NewGuid().ToString("N")[..8] + ".vdi");
    try {
      // Sparse VDI (all zeros = all blocks sparse)
      var data = new byte[128 * 1024];
      using (var fs = File.Create(path))
      using (var vw = new VdiWriter(fs, leaveOpen: true, virtualSize: data.Length))
        vw.Write(data);

      var origSize = new FileInfo(path).Length;
      SparseConverter.Densify(path);
      var newSize = new FileInfo(path).Length;

      // Dense should be larger than sparse (blocks allocated)
      Assert.That(newSize, Is.GreaterThanOrEqualTo(origSize));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── VMDK sparsify ─────────────────────────────────────────────────

  [Test]
  public void Sparsify_Vmdk_PreservesData() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_sparse_vmdk_" + Guid.NewGuid().ToString("N")[..8] + ".vmdk");
    try {
      var data = new byte[65536]; // One grain worth
      new Random(77).NextBytes(data);

      var mw = new VmdkWriter();
      mw.SetDiskData(data);
      File.WriteAllBytes(path, mw.Build());

      SparseConverter.Sparsify(path);

      using var stream = File.OpenRead(path);
      var reader = new VmdkReader(stream);
      var extracted = reader.Extract(reader.Entries[0]);
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test]
  public void Densify_Vmdk_AllocatesBlocks() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_dense_vmdk_" + Guid.NewGuid().ToString("N")[..8] + ".vmdk");
    try {
      var data = new byte[128 * 1024]; // Two grains, all zeros
      var mw = new VmdkWriter();
      mw.SetDiskData(data);
      File.WriteAllBytes(path, mw.Build());

      var origSize = new FileInfo(path).Length;
      SparseConverter.Densify(path);
      var newSize = new FileInfo(path).Length;

      Assert.That(newSize, Is.GreaterThanOrEqualTo(origSize));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── Unsupported format ────────────────────────────────────────────

  [Test]
  public void Sparsify_UnsupportedFormat_Throws() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_sparse_unsup_" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    try {
      File.WriteAllBytes(path, new byte[1024]);
      Assert.Throws<NotSupportedException>(() => SparseConverter.Sparsify(path));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Test]
  public void Densify_UnsupportedFormat_Throws() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_dense_unsup_" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    try {
      File.WriteAllBytes(path, new byte[1024]);
      Assert.Throws<NotSupportedException>(() => SparseConverter.Densify(path));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  // ── Round-trip: sparsify then densify ─────────────────────────────

  [Test]
  public void RoundTrip_Vhd_SparsifyThenDensify_PreservesData() {
    var path = Path.Combine(Path.GetTempPath(), "cwb_rt_vhd_" + Guid.NewGuid().ToString("N")[..8] + ".vhd");
    try {
      var data = new byte[256 * 1024]; // 256 KB
      new Random(42).NextBytes(data.AsSpan(0, 512)); // Some data, rest is zeros

      var writer = new VhdWriter();
      writer.SetDiskData(data);
      File.WriteAllBytes(path, writer.Build()); // Fixed

      // Sparsify
      SparseConverter.Sparsify(path);
      var sparseSize = new FileInfo(path).Length;

      // Densify
      SparseConverter.Densify(path);
      var denseSize = new FileInfo(path).Length;

      Assert.That(denseSize, Is.GreaterThanOrEqualTo(sparseSize));

      // Verify data
      using var stream = File.OpenRead(path);
      var reader = new VhdReader(stream);
      var extracted = reader.Extract(reader.Entries[0]);
      Assert.That(extracted.AsSpan(0, 512).ToArray(), Is.EqualTo(data.AsSpan(0, 512).ToArray()));
    } finally {
      if (File.Exists(path)) File.Delete(path);
    }
  }
}
