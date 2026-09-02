namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter8;

/// <summary>
/// Represents a page 9 0.
/// </summary>
public class Page9_0 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 3, 3, 9, 9, 9, 9, 9, 9, 4, 9, 9, 9, 9, 9, 9,
         9, 9, 5, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
         9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
         9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
         9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 8,
         8,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
        /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -511895552;
        /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1631393792;
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
