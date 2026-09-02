namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter1;

/// <summary>
/// Represents a page 7 1.
/// </summary>
public class Page7_1 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 6, 6, 6, 6, 7, 7, 8, 8, 9, 9, 5, 7, 7,
         8, 7, 7, 7, 9, 8,10, 9,10,11, 5, 7, 7, 8, 8, 7,
         7, 8, 9,10,10,11,11, 6, 8, 8, 9, 9, 9, 9,11,10,
        12,12,15,12, 6, 8, 8, 9, 9, 9, 9,11,11,12,11,14,
        12, 7, 8, 8,10,10,12,12,13,13,13,15,13,13, 7, 8,
         8,10,10,11,11,13,12,14,15,15,15, 9,10,10,11,12,
        13,13,14,15,14,15,14,15, 8,10,10,12,12,14,14,15,
        14,14,15,15,14,10,12,12,14,14,15,14,15,15,15,14,
        15,15,10,12,12,13,14,15,14,15,15,14,15,15,15,12,
        15,13,15,14,15,15,15,15,15,15,15,15,13,13,15,15,
        15,15,15,15,15,15,15,15,15,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -523010048;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1618608128;
        /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 4;
        /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

        /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        6,
        5,
        7,
        4,
        8,
        3,
        9,
        2,
        10,
        1,
        11,
        0,
        12,
    };
}
