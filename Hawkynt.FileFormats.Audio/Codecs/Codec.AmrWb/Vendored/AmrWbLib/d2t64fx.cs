
using System.Reflection.Emit;

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    /*-------------------------------------------------------------------*
     *                         D2T64FX.C                   *
     *-------------------------------------------------------------------*
     * 12 bits algebraic codebook decoder.                               *
     * 2 tracks x 32 positions per track = 64 samples.                   *
     *                                                                   *
     * 12 bits --> 2 pulses in a frame of 64 samples.                    *
     *                                                                   *
     * All pulses can have two (2) possible amplitudes: +1 or -1.        *
     * Each pulse can have 32 possible positions.                        *
     *                                                                   *
     * See dec2t64.c for more details of the algebraic code.             *
     *-------------------------------------------------------------------*/

    private unsafe void DEC_ACELP_2t64_fx(
         short index,                         /* (i) :    12 bits index                                  */
         short* code)                         /* (o) :Q9  algebraic (fixed) codebook excitation          */
    {
        //#define L_CODE    64                       /* codevector length  */
        //#define NB_TRACK  2                        /* number of track    */
        //#define NB_POS    32                       /* number of position */
        short L_CODE = 64;
        //short NB_TRACK = 2;
        short NB_POS = 32;
         
        short i, i0, i1;

        for (i = 0; i < L_CODE; i++)
        {
            code[i] = 0;
        }

        /* decode the positions and signs of pulses and build the codeword */

        i0 = (short)(shr(index, 5) & 0x003E);
        i1 = (short)(add(shl((short)(index & 0x001F), 1), 1));

        if ((shr(index, 6) & NB_POS) == 0)
            code[i0] = 512;
        else
            code[i0] = -512;

        if ((index & NB_POS) == 0)
            code[i1] = 512;
        else
            code[i1] = -512;
        return;
    }
}
