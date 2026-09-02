namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Uncoupled.Chapter2;

/// <summary>
/// Represents a page 8 0.
/// </summary>
public class Page8_0 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 7, 7, 8, 8, 7, 7, 9, 8,10, 9,11,11, 4,
         7, 6, 9, 8, 9, 9, 9, 9,10, 9,11, 9,12, 9, 4, 6,
         7, 8, 8, 9, 9, 9, 9,10,10,10,11,11,12, 7, 9, 8,
        10,10,11,11,10,10,11,11,12,12,13,12, 7, 8, 8,10,
        10,10,11,10,10,11,11,11,12,12,13, 8, 9, 9,11,11,
        11,11,11,11,12,12,13,13,13,13, 8, 9, 9,11,11,11,
        11,11,11,12,12,13,13,13,14, 8, 9, 9,10,10,11,11,
        12,11,13,13,14,13,14,14, 8, 9, 9,10,10,11,11,12,
        12,12,12,13,13,14,14, 9,10,10,11,11,12,12,13,12,
        13,13,14,14,15,15, 9,10,10,11,11,12,12,12,13,13,
        13,14,14,14,15,10,11,11,12,12,13,13,14,13,14,14,
        15,14,15,15,10,11,11,12,12,13,12,13,14,14,14,14,
        14,15,15,11,12,12,13,13,13,13,14,14,15,14,15,15,
        16,16,11,12,12,13,13,13,13,14,14,14,15,15,15,16,
        16,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -520986624;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1620377600;
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
        7,
        6,
        8,
        5,
        9,
        4,
        10,
        3,
        11,
        2,
        12,
        1,
        13,
        0,
        14,
    };
}
