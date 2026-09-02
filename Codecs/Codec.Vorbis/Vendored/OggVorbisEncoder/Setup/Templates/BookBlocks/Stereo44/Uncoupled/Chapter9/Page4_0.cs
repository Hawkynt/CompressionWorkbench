namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter9;

/// <summary>
/// Represents a page 4 0.
/// </summary>
public class Page4_0 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         4, 5, 5, 6, 6, 7, 7, 8, 8, 8, 8, 9, 9,10,10,11,
        11, 5, 5, 5, 6, 6, 7, 7, 8, 8, 8, 8, 9, 9,10,10,
        11,11, 5, 5, 5, 6, 6, 7, 7, 8, 8, 8, 8, 9, 9,10,
        10,11,11, 6, 6, 6, 7, 6, 7, 7, 8, 8, 9, 9,10,10,
        11,11,12,11, 6, 6, 6, 6, 7, 7, 7, 8, 8, 9, 9,10,
        10,11,11,11,12, 7, 7, 7, 7, 7, 8, 8, 9, 9, 9, 9,
        10,10,11,11,12,12, 7, 7, 7, 7, 7, 8, 8, 9, 9, 9,
         9,10,10,11,11,12,12, 8, 8, 8, 8, 8, 9, 8,10, 9,
        10,10,11,10,12,11,13,12, 8, 8, 8, 8, 8, 9, 9, 9,
        10,10,10,10,11,11,12,12,12, 8, 8, 8, 9, 9, 9, 9,
        10,10,11,10,12,11,12,12,13,12, 8, 8, 8, 9, 9, 9,
         9,10,10,10,11,11,11,12,12,12,13, 9, 9, 9,10,10,
        10,10,11,10,11,11,12,11,13,12,13,13, 9, 9,10,10,
        10,10,10,10,11,11,11,11,12,12,13,13,13,10,11,10,
        11,11,11,11,12,11,12,12,13,12,13,13,14,13,10,10,
        10,11,11,11,11,11,12,12,12,12,13,13,13,13,14,11,
        11,11,12,11,12,12,12,12,13,13,13,13,14,13,14,14,
        11,11,11,11,12,12,12,12,12,12,13,13,13,13,14,14,
        14,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -529530880;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1611661312;
        /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 5;
        /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

        /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        8,
        7,
        9,
        6,
        10,
        5,
        11,
        4,
        12,
        3,
        13,
        2,
        14,
        1,
        15,
        0,
        16,
    };
}
