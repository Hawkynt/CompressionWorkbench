/*------------------------------------------------------------------------*
 *                         AUTOCORR.C                                     *
 *------------------------------------------------------------------------*
 *   Compute autocorrelations of signal with windowing                    *
 *                                                                        *
 *------------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    private unsafe void Autocorr(
         short* x,                           /* (i)    : Input signal                      */
         short m,                             /* (i)    : LPC order                         */
         short[] r_h,                         /* (o) Q15: Autocorrelations  (msb)           */
         short[] r_l                          /* (o)    : Autocorrelations  (lsb)           */
)
    {
        short i, j, norm, shift;
        short[] y = new short[L_WINDOW];
        int L_sum, L_tmp;

        /* Windowing of signal */

        for (i = 0; i < L_WINDOW; i++)
        {
            y[i] = mult_r(x[i], window[i]); //move16();
        }

        /* calculate energy of signal */

        L_sum = L_deposit_h(16);               /* sqrt(256), avoid overflow after rounding */
        for (i = 0; i < L_WINDOW; i++)
        {
            L_tmp = L_mult(y[i], y[i]);
            L_tmp = L_shr(L_tmp, 8);
            L_sum = L_add(L_sum, L_tmp);
        }

        /* scale signal to avoid overflow in autocorrelation */

        norm = norm_l(L_sum);
        shift = sub(4, shr(norm, 1));
        //test();
        if (shift < 0)
        {
            shift = 0; //move16();
        }
        for (i = 0; i < L_WINDOW; i++)
        {
            y[i] = shr_r(y[i], shift); //move16();
        }

        /* Compute and normalize r[0] */

        L_sum = 1; //move32();
        for (i = 0; i < L_WINDOW; i++)
            L_sum = L_mac(L_sum, y[i], y[i]);

        norm = norm_l(L_sum);
        L_sum = L_shl(L_sum, norm);
        L_Extract(L_sum, ref r_h[0], ref r_l[0]);    /* Put in DPF format (see oper_32b) */

        /* Compute r[1] to r[m] */

        for (i = 1; i <= m; i++)
        {
            L_sum = 0; //move32();
            for (j = 0; j < L_WINDOW - i; j++)
                L_sum = L_mac(L_sum, y[j], y[j + i]);

            L_sum = L_shl(L_sum, norm);
            L_Extract(L_sum, ref r_h[i], ref r_l[i]);
        }

        return;
    }
}
