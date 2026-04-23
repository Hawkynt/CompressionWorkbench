using System.Buffers.Binary;
using System.IO.Compression;
using FileFormat.Nifti;

namespace Compression.Tests.Nifti;

[TestFixture]
public class NiftiTests {

  /// <summary>
  /// Builds a minimal little-endian NIfTI-1 file: 352-byte header (348 struct +
  /// 4 padding) plus the voxel payload. Writes <c>sizeof_hdr=348</c>,
  /// <c>dim[0..3]</c>, <c>datatype</c>, <c>bitpix</c>, <c>vox_offset</c>, and the
  /// <c>n+1\0</c> magic at offset 344.
  /// </summary>
  private static byte[] BuildNifti1(short datatype, short bitpix, short[] dims, byte[] voxels, string magic = "n+1\0") {
    const int headerSize = 352;
    var buf = new byte[headerSize + voxels.Length];

    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), 348);
    // dim: offset 40..56 (8 int16).
    for (var i = 0; i < Math.Min(dims.Length, 8); i++)
      BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(40 + i * 2, 2), dims[i]);
    // datatype @ 70, bitpix @ 72.
    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(70, 2), datatype);
    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(72, 2), bitpix);
    // pixdim @ 76..108 (8 float32) left at 0.
    // vox_offset @ 108 (float32 — offset of voxel data from file start).
    BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(108, 4), headerSize);
    BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(112, 4), 1f); // scl_slope
    BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(116, 4), 0f); // scl_inter
    // magic @ 344 (4 bytes).
    var magicBytes = System.Text.Encoding.ASCII.GetBytes(magic);
    magicBytes.AsSpan().CopyTo(buf.AsSpan(344, Math.Min(4, magicBytes.Length)));
    // payload.
    voxels.CopyTo(buf.AsSpan(headerSize));
    return buf;
  }

  private static byte[] BuildNifti2(short datatype, short bitpix, long[] dims, byte[] voxels) {
    const int headerSize = 540;
    var buf = new byte[headerSize + voxels.Length];
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), 540);
    System.Text.Encoding.ASCII.GetBytes("n+2\0").CopyTo(buf.AsSpan(4, 4));
    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(12, 2), datatype);
    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(14, 2), bitpix);
    for (var i = 0; i < Math.Min(dims.Length, 8); i++)
      BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16 + i * 8, 8), dims[i]);
    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(168, 8), headerSize);
    voxels.CopyTo(buf.AsSpan(headerSize));
    return buf;
  }

  [Test, Category("HappyPath")]
  public void NiftiReader_V1_ParsesHeaderAndExtractsVoxels() {
    var voxels = new byte[64];
    for (var i = 0; i < voxels.Length; i++) voxels[i] = (byte)(i & 0xFF);
    var data = BuildNifti1(datatype: 2, bitpix: 8, dims: [3, 4, 4, 4], voxels);

    var img = NiftiReader.Read(data);
    Assert.Multiple(() => {
      Assert.That(img.Version, Is.EqualTo(NiftiReader.NiftiVersion.Nifti1));
      Assert.That(img.LittleEndian, Is.True);
      Assert.That(img.Magic, Is.EqualTo("n+1\0"));
      Assert.That(img.Dim[0], Is.EqualTo(3));
      Assert.That(img.Dim[1], Is.EqualTo(4));
      Assert.That(img.Datatype, Is.EqualTo(2));
      Assert.That(img.Bitpix, Is.EqualTo(8));
      Assert.That(img.VoxOffset, Is.EqualTo(352));
      Assert.That(img.VoxelBytes, Is.EqualTo(voxels).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void NiftiReader_V2_ParsesHeader() {
    var voxels = new byte[32];
    var data = BuildNifti2(datatype: 16, bitpix: 32, dims: [3, 2, 2, 2], voxels);
    var img = NiftiReader.Read(data);
    Assert.That(img.Version, Is.EqualTo(NiftiReader.NiftiVersion.Nifti2));
    Assert.That(img.SizeOfHeader, Is.EqualTo(540));
    Assert.That(img.Dim[1], Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void NiftiReader_BigEndianSizeofHdr_Detected() {
    // Build a pure big-endian header: sizeof_hdr=348 stored BE, magic "n+1".
    var buf = new byte[352];
    BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), 348);
    System.Text.Encoding.ASCII.GetBytes("n+1\0").CopyTo(buf.AsSpan(344, 4));
    var img = NiftiReader.Read(buf);
    Assert.That(img.LittleEndian, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void NiftiReader_InvalidSizeofHdr_Throws() {
    var buf = new byte[400];
    // 99 is not 348 or 540 in either byte order.
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), 99);
    Assert.That(() => NiftiReader.Read(buf), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void NiftiDescriptor_ListEmitsMetadataHeaderVoxels() {
    var voxels = new byte[32];
    var data = BuildNifti1(datatype: 4, bitpix: 16, dims: [2, 4, 4], voxels);

    using var ms = new MemoryStream(data);
    var entries = new NiftiFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("header.bin"));
      Assert.That(names, Does.Contain("voxels.bin"));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NiftiDescriptor_ExtractWritesVoxels() {
    var voxels = new byte[16];
    Array.Fill<byte>(voxels, 0xEE);
    var data = BuildNifti1(datatype: 2, bitpix: 8, dims: [1, 16], voxels);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new NiftiFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "voxels.bin")), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "voxels.bin")), Is.EqualTo(voxels).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void NiftiDescriptor_GzippedInput_IsTransparentlyInflated() {
    var voxels = new byte[32];
    for (var i = 0; i < voxels.Length; i++) voxels[i] = (byte)(i * 3);
    var plain = BuildNifti1(datatype: 2, bitpix: 8, dims: [1, 32], voxels);

    using var gz = new MemoryStream();
    using (var g = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
      g.Write(plain);
    gz.Position = 0;

    var entries = new NiftiFormatDescriptor().List(gz, null);
    var voxelEntry = entries.FirstOrDefault(e => e.Name == "voxels.bin");
    Assert.That(voxelEntry, Is.Not.Null);
    Assert.That(voxelEntry!.OriginalSize, Is.EqualTo(voxels.Length));
  }

  [Test, Category("EdgeCase")]
  public void NiftiDescriptor_TruncatedInput_EmitsErrorMetadata() {
    var tiny = new byte[8];
    using var ms = new MemoryStream(tiny);
    var entries = new NiftiFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
