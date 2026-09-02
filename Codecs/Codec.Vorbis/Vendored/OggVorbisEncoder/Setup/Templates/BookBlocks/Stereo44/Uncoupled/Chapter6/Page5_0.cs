namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter6;

/// <summary>
/// Represents a page 5 0.
/// </summary>
public class Page5_0 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         2, 3, 3, 6, 6, 8, 8,10,10, 4, 5, 5, 8, 7, 8, 8,
        11,11, 3, 5, 5, 7, 8, 8, 8,11,11, 6, 8, 7, 9, 9,
        10, 9,12,11, 6, 7, 8, 9, 9, 9,10,11,12, 8, 8, 8,
        10, 9,12,11,13,13, 8, 8, 9, 9,10,11,12,13,13,10,
        11,11,12,12,13,13,14,14,10,10,11,11,12,13,13,14,
        14,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -531628032;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1611661312;
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
        4,
        3,
        5,
        2,
        6,
        1,
        7,
        0,
        8,
    };
}
