/*-----------------------------------------------------------------------*
 *                         HP50.C                     *
 *-----------------------------------------------------------------------*
 * 2nd order high pass filter with cut off frequency at 31 Hz.           *
 * Designed with cheby2 function in MATLAB.                              *
 * Optimized for fixed-point to get the following frequency response:    *
 *                                                                       *
 *  frequency:     0Hz    14Hz  24Hz   31Hz   37Hz   41Hz   47Hz         *
 *  dB loss:     -infdB  -15dB  -6dB   -3dB  -1.5dB  -1dB  -0.5dB        *
 *                                                                       *
 * Algorithm:                                                            *
 *                                                                       *
 *  y[i] = hp_50b[0]*x[i] + hp_50b[1]*x[i-1] + hp_50b[2]*x[i-2]                         *
 *                   + a[1]*y[i-1] + a[2]*y[i-2];                        *
 *                                                                       *
 *  short hp_50b[3] = {4053, -8106, 4053};       in Q12                     *
 *  short a[3] = {8192, 16211, -8021};       in Q12                     *
 *                                                                       *
 *  float -->   hp_50b[3] = {0.989501953, -1.979003906,  0.989501953};        *
 *              a[3] = {1.000000000,  1.978881836, -0.979125977};        *
 *-----------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "oper_32b.h"
//#include "cnst.h"
//#include "acelp.h"
//#include "count.h"

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    /* filter coefficients  */
    static short[] hp50_b = new short[3] { 4053, -8106, 4053 };  /* Q12 */
    static short[] hp50_a = new short[3] { 8192, 16211, -8021 }; /* Q12 (x2) */

    /* Initialization of static values */

    void Init_HP50_12k8(short[] mem)
    {
        Set_zero(mem, 6);
    }

    private unsafe void HP50_12k8(
         short* signal,                      /* input/output signal */
         short lg,                            /* lenght of signal    */
         short[] mem)                          /* filter memory [6]   */
    {
        short i, x2;
        short y2_hi, y2_lo, y1_hi, y1_lo, x0, x1;
        int L_tmp;

        y2_hi = mem[0];
        y2_lo = mem[1];
        y1_hi = mem[2];
        y1_lo = mem[3];
        x0 = mem[4];
        x1 = mem[5];

        for (i = 0; i < lg; i++)
        {
            x2 = x1;
            x1 = x0;
            x0 = signal[i];

            /* y[i] = hp_50b[0]*x[i] + hp_50b[1]*x[i-1] + b140[2]*x[i-2]  */
            /* + a[1]*y[i-1] + a[2] * y[i-2];  */

            L_tmp = 16384;                    /* rounding to maximise precision */
            L_tmp = L_mac(L_tmp, y1_lo, hp50_a[1]);
            L_tmp = L_mac(L_tmp, y2_lo, hp50_a[2]);
            L_tmp = L_shr(L_tmp, 15);
            L_tmp = L_mac(L_tmp, y1_hi, hp50_a[1]);
            L_tmp = L_mac(L_tmp, y2_hi, hp50_a[2]);
            L_tmp = L_mac(L_tmp, x0, hp50_b[0]);
            L_tmp = L_mac(L_tmp, x1, hp50_b[1]);
            L_tmp = L_mac(L_tmp, x2, hp50_b[2]);

            L_tmp = L_shl(L_tmp, 2);           /* coeff Q12 --> Q14 */

            y2_hi = y1_hi;
            y2_lo = y1_lo;
            L_Extract(L_tmp, ref y1_hi, ref y1_lo);

            L_tmp = L_shl(L_tmp, 1);           /* coeff Q14 --> Q15 with saturation */
            signal[i] = round(L_tmp);
        }

        mem[0] = y2_hi;
        mem[1] = y2_lo;
        mem[2] = y1_hi;
        mem[3] = y1_lo;
        mem[4] = x0;
        mem[5] = x1;
    }

    // 30 Apr 25 PHR
    private void HP50_12k8(
         short[] signal,                      /* input/output signal */
         short lg,                            /* lenght of signal    */
         short[] mem)                          /* filter memory [6]   */
    {
        short i, x2;
        short y2_hi, y2_lo, y1_hi, y1_lo, x0, x1;
        int L_tmp;

        y2_hi = mem[0];
        y2_lo = mem[1];
        y1_hi = mem[2];
        y1_lo = mem[3];
        x0 = mem[4];
        x1 = mem[5];

        for (i = 0; i < lg; i++)
        {
            x2 = x1;
            x1 = x0;
            x0 = signal[i];

            /* y[i] = hp_50b[0]*x[i] + hp_50b[1]*x[i-1] + b140[2]*x[i-2]  */
            /* + a[1]*y[i-1] + a[2] * y[i-2];  */

            L_tmp = 16384;                    /* rounding to maximise precision */
            L_tmp = L_mac(L_tmp, y1_lo, hp50_a[1]);
            L_tmp = L_mac(L_tmp, y2_lo, hp50_a[2]);
            L_tmp = L_shr(L_tmp, 15);
            L_tmp = L_mac(L_tmp, y1_hi, hp50_a[1]);
            L_tmp = L_mac(L_tmp, y2_hi, hp50_a[2]);
            L_tmp = L_mac(L_tmp, x0, hp50_b[0]);
            L_tmp = L_mac(L_tmp, x1, hp50_b[1]);
            L_tmp = L_mac(L_tmp, x2, hp50_b[2]);

            L_tmp = L_shl(L_tmp, 2);           /* coeff Q12 --> Q14 */

            y2_hi = y1_hi;
            y2_lo = y1_lo;
            L_Extract(L_tmp, ref y1_hi, ref y1_lo);

            L_tmp = L_shl(L_tmp, 1);           /* coeff Q14 --> Q15 with saturation */
            signal[i] = round(L_tmp);
        }

        mem[0] = y2_hi;
        mem[1] = y2_lo;
        mem[2] = y1_hi;
        mem[3] = y1_lo;
        mem[4] = x0;
        mem[5] = x1;
    }

    // 30 Apr 25 PHE
    private unsafe void HP50_12k8(
         short* signal,                      /* input/output signal */
         short lg,                            /* lenght of signal    */
         short* mem)                          /* filter memory [6]   */
    {
        short i, x2;
        short y2_hi, y2_lo, y1_hi, y1_lo, x0, x1;
        int L_tmp;

        y2_hi = mem[0];
        y2_lo = mem[1];
        y1_hi = mem[2];
        y1_lo = mem[3];
        x0 = mem[4];
        x1 = mem[5];

        for (i = 0; i < lg; i++)
        {
            x2 = x1;
            x1 = x0;
            x0 = signal[i];

            /* y[i] = hp_50b[0]*x[i] + hp_50b[1]*x[i-1] + b140[2]*x[i-2]  */
            /* + a[1]*y[i-1] + a[2] * y[i-2];  */

            L_tmp = 16384;                    /* rounding to maximise precision */
            L_tmp = L_mac(L_tmp, y1_lo, hp50_a[1]);
            L_tmp = L_mac(L_tmp, y2_lo, hp50_a[2]);
            L_tmp = L_shr(L_tmp, 15);
            L_tmp = L_mac(L_tmp, y1_hi, hp50_a[1]);
            L_tmp = L_mac(L_tmp, y2_hi, hp50_a[2]);
            L_tmp = L_mac(L_tmp, x0, hp50_b[0]);
            L_tmp = L_mac(L_tmp, x1, hp50_b[1]);
            L_tmp = L_mac(L_tmp, x2, hp50_b[2]);

            L_tmp = L_shl(L_tmp, 2);           /* coeff Q12 --> Q14 */

            y2_hi = y1_hi;
            y2_lo = y1_lo;
            L_Extract(L_tmp, ref y1_hi, ref y1_lo);

            L_tmp = L_shl(L_tmp, 1);           /* coeff Q14 --> Q15 with saturation */
            signal[i] = round(L_tmp);
        }

        mem[0] = y2_hi;
        mem[1] = y2_lo;
        mem[2] = y1_hi;
        mem[3] = y1_lo;
        mem[4] = x0;
        mem[5] = x1;
    }
}
