namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Uncoupled.Chapter2;

/// <summary>
/// Represents a chapter 2 short.
/// </summary>
public class Chapter2Short : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         8,11,13,13,15,16,19,19,19,19,11, 8, 8, 9, 9,11,
        13,15,19,20,14, 8, 7, 7, 8, 9,12,13,15,20,15, 9,
         6, 5, 5, 7,10,12,14,18,14, 9, 7, 5, 3, 4, 7,10,
        12,16,13,10, 8, 6, 3, 3, 5, 8,11,14,11,10, 9, 7,
         5, 4, 4, 6,11,14,10,10,10, 8, 6, 5, 5, 6,10,14,
        10,10,10, 9, 8, 7, 7, 7,10,14,11,12,12,12,11,10,
        10,10,12,16,
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
