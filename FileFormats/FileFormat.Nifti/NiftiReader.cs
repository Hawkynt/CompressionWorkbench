#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Nifti;

/// <summary>
/// Reader for NIfTI-1 and NIfTI-2 medical imaging files (single-file <c>.nii</c>
/// variant). Parses the 352-byte (NIfTI-1) or 540-byte (NIfTI-2) header and
/// separates the voxel payload following <c>vox_offset</c>.
/// </summary>
/// <remarks>
/// Format reference: https://nifti.nimh.nih.gov/nifti-1/. NIfTI-1 places its
/// <c>magic</c> at byte 344 (one of <c>n+1\0</c> for single-file or <c>ni1\0</c>
/// for split <c>.hdr+.img</c>). NIfTI-2 places its <c>magic</c> at byte 4
/// (<c>n+2\0</c> / <c>ni2\0</c>) and starts with a 540-byte <c>sizeof_hdr</c>.
/// Endianness is determined by the sentinel <c>sizeof_hdr</c> value (348 for
/// NIfTI-1, 540 for NIfTI-2) — if the raw little-endian read yields a valid
/// header size the file is little-endian; if byte-swapping yields it the file is
/// big-endian. The <c>dim[0]</c> field doubles as an endianness sanity check:
/// a value outside [1,7] indicates the other byte order.
/// </remarks>
public sealed class NiftiReader {

  public enum NiftiVersion { Nifti1 = 1, Nifti2 = 2 }

  /// <summary>Parsed NIfTI file — header, raw header bytes, voxels, and key metadata.</summary>
  public sealed record NiftiImage(
    NiftiVersion Version,
    bool LittleEndian,
    string Magic,
    int SizeOfHeader,
    int Datatype,
    int Bitpix,
    long[] Dim,              // dim[0] = rank, dim[1..7] = sizes
    double[] Pixdim,         // pixdim[0] = qfac, pixdim[1..] = voxel spacing
    long VoxOffset,
    double SclSlope,
    double SclInter,
    string Description,      // descrip field (80 bytes for NIfTI-1, 80 for NIfTI-2)
    string IntentName,       // intent_name (16 bytes)
    byte[] HeaderBytes,      // raw header (348+4 = 352 for NIfTI-1, 540 for NIfTI-2)
    byte[] VoxelBytes        // raw voxel data from vox_offset onward (as much as is present)
  );

  /// <summary>Parses an in-memory <c>.nii</c> (single-file variant).</summary>
  public static NiftiImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < 348) throw new InvalidDataException("nifti: file shorter than 348-byte minimum header.");

    // Peek sizeof_hdr in both byte orders to decide version + endianness.
    var sizeLe = BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
    var sizeBe = BinaryPrimitives.ReadInt32BigEndian(data[..4]);

    NiftiVersion version;
    bool littleEndian;
    if (sizeLe == 348) { version = NiftiVersion.Nifti1; littleEndian = true; }
    else if (sizeBe == 348) { version = NiftiVersion.Nifti1; littleEndian = false; }
    else if (sizeLe == 540) { version = NiftiVersion.Nifti2; littleEndian = true; }
    else if (sizeBe == 540) { version = NiftiVersion.Nifti2; littleEndian = false; }
    else throw new InvalidDataException($"nifti: sizeof_hdr is neither 348 nor 540 (got LE={sizeLe}, BE={sizeBe})");

    return version == NiftiVersion.Nifti1
      ? ReadNifti1(data, littleEndian)
      : ReadNifti2(data, littleEndian);
  }

  // NIfTI-1 layout (348 bytes): sizeof_hdr[4] + data_type[10] + db_name[18] + extents[4] + session_error[2] +
  // regular[1] + dim_info[1] + dim[16] (8 i16) + intent_p1[4] + intent_p2[4] + intent_p3[4] + intent_code[2] +
  // datatype[2] + bitpix[2] + slice_start[2] + pixdim[32] (8 f32) + vox_offset[4] + scl_slope[4] + scl_inter[4] +
  // slice_end[2] + slice_code[1] + xyzt_units[1] + cal_max[4] + cal_min[4] + slice_duration[4] + toffset[4] +
  // glmax[4] + glmin[4] + descrip[80] + aux_file[24] + qform_code[2] + sform_code[2] + quatern_b/c/d[12] +
  // qoffset_x/y/z[12] + srow_x/y/z[48] + intent_name[16] + magic[4].
  // Headers are always 352 bytes on disk (348 + 4 pad) so the descriptor entry covers the full header region.
  private static NiftiImage ReadNifti1(ReadOnlySpan<byte> data, bool le) {
    var dim = new long[8];
    for (var i = 0; i < 8; i++) dim[i] = ReadI16(data[(40 + i * 2)..], le);

    var datatype = ReadI16(data[70..], le);
    var bitpix = ReadI16(data[72..], le);

    var pixdim = new double[8];
    for (var i = 0; i < 8; i++) pixdim[i] = ReadF32(data[(76 + i * 4)..], le);

    var voxOffset = (long)ReadF32(data[108..], le);
    var sclSlope = ReadF32(data[112..], le);
    var sclInter = ReadF32(data[116..], le);

    var descrip = ReadNullTermAscii(data.Slice(148, 80));
    var intentName = ReadNullTermAscii(data.Slice(328, 16));
    var magic = Encoding.ASCII.GetString(data.Slice(344, 4));

    // The NIfTI-1 header is 348 bytes + 4 padding, so the on-disk header region is 352 bytes.
    const int headerSize = 352;
    var effectiveHeader = Math.Min(headerSize, data.Length);
    var headerBytes = data[..effectiveHeader].ToArray();

    var bodyStart = (int)Math.Min(data.Length, Math.Max(voxOffset, headerSize));
    var voxels = data[bodyStart..].ToArray();

    return new NiftiImage(
      Version: NiftiVersion.Nifti1,
      LittleEndian: le,
      Magic: magic,
      SizeOfHeader: 348,
      Datatype: datatype,
      Bitpix: bitpix,
      Dim: dim,
      Pixdim: pixdim,
      VoxOffset: voxOffset,
      SclSlope: sclSlope,
      SclInter: sclInter,
      Description: descrip,
      IntentName: intentName,
      HeaderBytes: headerBytes,
      VoxelBytes: voxels);
  }

  // NIfTI-2 layout (540 bytes): sizeof_hdr[4] + magic[8] + datatype[2] + bitpix[2] + dim[64] (8 i64) +
  // intent_p1/2/3[24] + pixdim[64] (8 f64) + vox_offset[8] + scl_slope[8] + scl_inter[8] + cal_max[8] +
  // cal_min[8] + slice_duration[8] + toffset[8] + slice_start[8] + slice_end[8] + descrip[80] + aux_file[24] +
  // qform_code[4] + sform_code[4] + quatern_b/c/d[24] + qoffset_x/y/z[24] + srow_x/y/z[192] +
  // slice_code[4] + xyzt_units[4] + intent_code[4] + intent_name[16] + dim_info[1] + unused_str[15].
  private static NiftiImage ReadNifti2(ReadOnlySpan<byte> data, bool le) {
    if (data.Length < 540) throw new InvalidDataException("nifti-2: file shorter than 540-byte header.");

    var magic = Encoding.ASCII.GetString(data.Slice(4, 4));
    var datatype = ReadI16(data[12..], le);
    var bitpix = ReadI16(data[14..], le);

    var dim = new long[8];
    for (var i = 0; i < 8; i++) dim[i] = ReadI64(data[(16 + i * 8)..], le);

    var pixdim = new double[8];
    for (var i = 0; i < 8; i++) pixdim[i] = ReadF64(data[(104 + i * 8)..], le);

    var voxOffset = ReadI64(data[168..], le);
    var sclSlope = ReadF64(data[176..], le);
    var sclInter = ReadF64(data[184..], le);

    var descrip = ReadNullTermAscii(data.Slice(240, 80));
    var intentName = ReadNullTermAscii(data.Slice(508, 16));

    const int headerSize = 540;
    var effectiveHeader = Math.Min(headerSize, data.Length);
    var headerBytes = data[..effectiveHeader].ToArray();

    var bodyStart = (int)Math.Min(data.Length, Math.Max(voxOffset, headerSize));
    var voxels = data[bodyStart..].ToArray();

    return new NiftiImage(
      Version: NiftiVersion.Nifti2,
      LittleEndian: le,
      Magic: magic,
      SizeOfHeader: 540,
      Datatype: datatype,
      Bitpix: bitpix,
      Dim: dim,
      Pixdim: pixdim,
      VoxOffset: voxOffset,
      SclSlope: sclSlope,
      SclInter: sclInter,
      Description: descrip,
      IntentName: intentName,
      HeaderBytes: headerBytes,
      VoxelBytes: voxels);
  }

  private static short ReadI16(ReadOnlySpan<byte> s, bool le) =>
    le ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s);
  private static long ReadI64(ReadOnlySpan<byte> s, bool le) =>
    le ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s);
  private static float ReadF32(ReadOnlySpan<byte> s, bool le) =>
    le ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s);
  private static double ReadF64(ReadOnlySpan<byte> s, bool le) =>
    le ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s);

  private static string ReadNullTermAscii(ReadOnlySpan<byte> s) {
    var len = s.IndexOf((byte)0);
    if (len < 0) len = s.Length;
    return Encoding.ASCII.GetString(s[..len]).Trim();
  }

  /// <summary>
  /// Short human-readable name for the NIfTI datatype code.
  /// See <c>nifti1.h</c> DT_* constants.
  /// </summary>
  public static string DatatypeName(int code) => code switch {
    0 => "UNKNOWN",
    1 => "BINARY",
    2 => "UINT8",
    4 => "INT16",
    8 => "INT32",
    16 => "FLOAT32",
    32 => "COMPLEX64",
    64 => "FLOAT64",
    128 => "RGB24",
    256 => "INT8",
    512 => "UINT16",
    768 => "UINT32",
    1024 => "INT64",
    1280 => "UINT64",
    1536 => "FLOAT128",
    1792 => "COMPLEX128",
    2048 => "COMPLEX256",
    2304 => "RGBA32",
    _ => $"unknown_{code}",
  };
}
