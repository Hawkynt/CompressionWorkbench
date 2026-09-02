namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter9;

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
         1, 4, 4, 7, 6, 8, 8, 8, 8, 9, 9,10,10,11,10, 6,
         5, 5, 7, 7, 9, 9, 8, 9,10,10,11,11,12,12, 6, 5,
         5, 7, 7, 9, 9, 9, 9,10,10,11,11,12,12,21, 7, 8,
         8, 8, 9, 9, 9, 9,10,10,11,11,12,12,21, 8, 8, 8,
         8, 9, 9, 9, 9,10,10,11,11,12,12,21,11,12, 9, 9,
        10,10,10,10,10,11,11,12,12,12,21,12,12, 9, 8,10,
        10,10,10,11,11,12,12,13,13,21,21,21, 9, 9, 9, 9,
        11,11,11,11,12,12,12,13,21,20,20, 9, 9, 9, 9,10,
        11,11,11,12,12,13,13,20,20,20,13,13,10,10,11,11,
        12,12,13,13,13,13,20,20,20,13,13,10,10,11,11,12,
        12,13,13,13,13,20,20,20,20,20,12,12,12,12,12,12,
        13,13,14,14,20,20,20,20,20,12,12,12,11,13,12,13,
        13,14,14,20,20,20,20,20,15,16,13,12,13,13,14,13,
        14,14,20,20,20,20,20,16,15,12,12,13,12,14,13,14,
        14,
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
