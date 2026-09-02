namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapter0;

/// <summary>
/// Represents a managed chapter 0 long.
/// </summary>
public class ManagedChapter0Long : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         5, 4, 9,10, 9,10,11,12,13, 4, 1, 5, 7, 7, 9,11,
        12,14, 8, 5, 7, 9, 8,10,13,13,13,10, 7, 9, 4, 6,
         7,10,12,14, 9, 6, 7, 6, 6, 7,10,12,12, 9, 8, 9,
         7, 6, 7, 8,11,12,11,11,11, 9, 8, 7, 8,10,12,12,
        13,14,12,11, 9, 9, 9,12,12,17,17,15,16,12,10,11,
        13,
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
