namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 256 x 4 low class 0.
/// </summary>
public class Line256X4LowClass0 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        4, 5, 6, 11, 5, 5, 6, 10, 7, 7, 6, 6, 14, 13, 9, 9,
        6, 6, 6, 10, 6, 6, 6, 9, 8, 7, 7, 9, 14, 12, 8, 11,
        8, 7, 7, 11, 8, 8, 7, 11, 9, 9, 7, 9, 13, 11, 9, 13,
        19, 19, 18, 19, 15, 16, 16, 19, 11, 11, 10, 13, 10, 10, 9, 15,
        5, 5, 6, 13, 6, 6, 6, 11, 8, 7, 6, 7, 14, 11, 10, 11,
        6, 6, 6, 12, 7, 6, 6, 11, 8, 7, 7, 11, 13, 11, 9, 11,
        9, 7, 6, 12, 8, 7, 6, 12, 9, 8, 8, 11, 13, 10, 7, 13,
        19, 19, 17, 19, 17, 14, 14, 19, 12, 10, 8, 12, 13, 10, 9, 16,
        7, 8, 7, 12, 7, 7, 7, 11, 8, 7, 7, 8, 12, 12, 11, 11,
        8, 8, 7, 12, 8, 7, 6, 11, 8, 7, 7, 10, 10, 11, 10, 11,
        9, 8, 8, 13, 9, 8, 7, 12, 10, 9, 7, 11, 9, 8, 7, 11,
        18, 18, 15, 18, 18, 16, 17, 18, 15, 11, 10, 18, 11, 9, 9, 18,
        16, 16, 13, 16, 12, 11, 10, 16, 12, 11, 9, 6, 15, 12, 11, 13,
        16, 16, 14, 14, 13, 11, 12, 16, 12, 9, 9, 13, 13, 10, 10, 12,
        17, 18, 17, 17, 14, 15, 14, 16, 14, 12, 14, 15, 12, 10, 11, 12,
        18, 18, 18, 18, 18, 18, 18, 18, 18, 12, 13, 18, 16, 11, 9, 18
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = CodeBookMapType.None;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = 0;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 0;
        /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 0;
        /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;
        /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = null;
}
