/*-------------------------------------------------------------------*
 *                         DEEMPH.C                   *
 *-------------------------------------------------------------------*
 * Deemphasis: filtering through 1/(1-mu z^-1)                 *
 *                                   *
 * Deemph2   --> signal is divided by 2.               *
 * Deemph_32 --> for 32 bits signal.                 *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    private void Deemph(
         short[] x,                           /* (i/o)   : input signal overwritten by the output */
         short mu,                            /* (i) Q15 : deemphasis factor                      */
         short L,                             /* (i)     : vector size                            */
         ref short mem)                          /* (i/o)   : memory (y[-1])                         */
    {
        short i;
        int L_tmp;

        L_tmp = L_deposit_h(x[0]);
        L_tmp = L_mac(L_tmp, mem, mu);
        x[0] = round(L_tmp);

        for (i = 1; i < L; i++)
        {
            L_tmp = L_deposit_h(x[i]);
            L_tmp = L_mac(L_tmp, x[i - 1], mu);
            x[i] = round(L_tmp);
        }

        mem = x[L - 1];
    }


    private unsafe void Deemph2(
         short* x,                           /* (i/o)   : input signal overwritten by the output */
         short mu,                            /* (i) Q15 : deemphasis factor                      */
         short L,                             /* (i)     : vector size                            */
         ref short mem                          /* (i/o)   : memory (y[-1])                         */
    )
    {
        short i;
        int L_tmp;

        /* saturation can occur in L_mac() */

        L_tmp = L_mult(x[0], 16384);
        L_tmp = L_mac(L_tmp, mem, mu);
        x[0] = round(L_tmp);

        for (i = 1; i < L; i++)
        {
            L_tmp = L_mult(x[i], 16384);
            L_tmp = L_mac(L_tmp, x[i - 1], mu);
            x[i] = round(L_tmp);
        }

        mem = x[L - 1];
    }

    private unsafe void Deemph_32(
         short* x_hi,                        /* (i)     : input signal (bit31..16) */
         short* x_lo,                        /* (i)     : input signal (bit15..4)  */
         short[] y,                           /* (o)     : output signal (x16)      */
         short mu,                            /* (i) Q15 : deemphasis factor        */
         short L,                             /* (i)     : vector size              */
         ref short mem)                          /* (i/o)   : memory (y[-1])           */
    {
        short i, fac;
        int L_tmp;

        fac = shr(mu, 1);                      /* Q15 --> Q14 */

        /* L_tmp = hi<<16 + lo<<4 */

        L_tmp = L_deposit_h(x_hi[0]);
        L_tmp = L_mac(L_tmp, x_lo[0], 8);
        L_tmp = L_shl(L_tmp, 3);
        L_tmp = L_mac(L_tmp, mem, fac);
        L_tmp = L_shl(L_tmp, 1);               /* saturation can occur here */
        y[0] = round(L_tmp);

        for (i = 1; i < L; i++)
        {
            L_tmp = L_deposit_h(x_hi[i]);
            L_tmp = L_mac(L_tmp, x_lo[i], 8);
            L_tmp = L_shl(L_tmp, 3);
            L_tmp = L_mac(L_tmp, y[i - 1], fac);
            L_tmp = L_shl(L_tmp, 1);           /* saturation can occur here */
            y[i] = round(L_tmp);
        }

        mem = y[L - 1];
    }


}
