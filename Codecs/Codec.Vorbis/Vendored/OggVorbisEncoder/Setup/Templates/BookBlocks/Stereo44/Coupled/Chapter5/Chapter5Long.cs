namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter5;

/// <summary>
/// Represents a chapter 5 long.
/// </summary>
public class Chapter5Long : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         3, 8, 9,13,10,12,12,12,12,12, 6, 4, 6, 8, 6, 8,
        10,10,11,12, 8, 5, 4,10, 4, 7, 8, 9,10,11,13, 8,
        10, 8, 9, 9,11,12,13,14,10, 6, 4, 9, 3, 5, 6, 8,
        10,11,11, 8, 6, 9, 5, 5, 6, 7, 9,11,12, 9, 7,11,
         6, 6, 6, 7, 8,10,12,11, 9,12, 7, 7, 6, 6, 7, 9,
        13,12,10,13, 9, 8, 7, 7, 7, 8,11,15,11,15,11,10,
         9, 8, 7, 7,
    };

        /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
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
