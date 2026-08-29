/*-------------------------------------------------------------------*
 *                         HP6K.C                   *
 *-------------------------------------------------------------------*
 * 15th order band pass 6kHz to 7kHz FIR filter.                     *
 *                                                                   *
 * frequency:  4kHz   5kHz  5.5kHz  6kHz  6.5kHz 7kHz  7.5kHz  8kHz  *
 * dB loss:   -60dB  -45dB  -13dB   -3dB   0dB   -3dB  -13dB  -45dB  *
 *-------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "acelp.h"
//#include "count.h"
//#include "cnst.h"

namespace AmrWbLib;

public partial class AmrWb
{
    private const int L_FIR = 31;

    /* filter coefficients (gain=4.0) */
    private static short[] fir_6k_7k = new short[L_FIR]
    {
        -32, 47, 32, -27, -369,
        1122, -1421, 0, 3798, -8880,
        12349, -10984, 3548, 7766, -18001,
        22118, -18001, 7766, 3548, -10984,
        12349, -8880, 3798, 0, -1421,
        1122, -369, -27, 32, 47,
        -32
    };

    private void Init_Filt_6k_7k(short[] mem)         /* mem[30] */
    {
        Set_zero(mem, L_FIR - 1);
    }

    private unsafe void Filt_6k_7k(
         short[] signal,                      /* input:  signal                  */
         short lg,                            /* input:  length of input         */
         short[] mem)                          /* in/out: memory (size=30)        */
    {
        short i, j;
        short* x = stackalloc short[L_SUBFR16k + (L_FIR - 1)];
        int L_tmp;

        Copy(mem, x, L_FIR - 1);

        for (i = 0; i < lg; i++)
        {
            x[i + L_FIR - 1] = shr(signal[i], 2);  /* gain of filter = 4 */
        }

        for (i = 0; i < lg; i++)
        {
            L_tmp = 0;
            for (j = 0; j < L_FIR; j++)
                L_tmp = L_mac(L_tmp, x[i + j], fir_6k_7k[j]);
            signal[i] = round(L_tmp);
        }

        Copy(x + lg, mem, L_FIR - 1);
    }

    // 30 Apr 25 PHR
    private unsafe void Filt_6k_7k(
         short* signal,                      /* input:  signal                  */
         short lg,                            /* input:  length of input         */
         short[] mem)                          /* in/out: memory (size=30)        */
    {
        short i, j;
        short* x = stackalloc short[L_SUBFR16k + (L_FIR - 1)];
        int L_tmp;

        Copy(mem, x, L_FIR - 1);

        for (i = 0; i < lg; i++)
        {
            x[i + L_FIR - 1] = shr(signal[i], 2);  /* gain of filter = 4 */
        }

        for (i = 0; i < lg; i++)
        {
            L_tmp = 0;
            for (j = 0; j < L_FIR; j++)
                L_tmp = L_mac(L_tmp, x[i + j], fir_6k_7k[j]);
            signal[i] = round(L_tmp);
        }

        Copy(x + lg, mem, L_FIR - 1);
    }

}
