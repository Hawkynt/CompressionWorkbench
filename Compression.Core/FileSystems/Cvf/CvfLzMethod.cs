namespace Compression.Core.FileSystems.Cvf;

/// <summary>
/// Per-cluster compression methods of the DoubleSpace/DriveSpace/Stacker CVF family (see
/// <c>docs/CVF-CODECS-ON-DISK.md</c>). <see cref="Sd3"/> and <see cref="Sd4"/> are the Stacker 3 and
/// Stacker 4 codecs.
/// </summary>
public enum CvfLzMethod {
  /// <summary>No compression; the cluster is stored as is.</summary>
  Stored = 0,
  /// <summary>DoubleSpace DS-0-x LZ77.</summary>
  Ds = 1,
  /// <summary>DriveSpace JM-0-x LZ77 (DriveSpace 3 <i>Normal</i>/<i>High</i>).</summary>
  Jm = 2,
  /// <summary>Per cluster the smaller of <see cref="Ds"/> and <see cref="Jm"/>, both of which every MSDBL volume reads.</summary>
  Auto = 3,
  /// <summary>DriveSpace 3 SQ-0-0 (<i>Ultra</i>): a raw DEFLATE stream behind the method header.</summary>
  Sq = 4,
  /// <summary>Stacker 4 SD-4: Huffman-coded LZ77 behind the header word <c>0x0081</c>.</summary>
  Sd4 = 5,
  /// <summary>Stacker 3 SD-3: a bare LZS stream closed by a check byte.</summary>
  Sd3 = 6,
}
