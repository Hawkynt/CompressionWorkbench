
namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private unsafe void agc2(
         short* sig_in,                      /* (i)     : postfilter input signal  */
         short[] sig_out,                     /* (i/o)   : postfilter output signal */
         short l_trm                          /* (i)     : subframe size            */
    )
    {

        short i, exp;
        short gain_in, gain_out, g0;
        int s;

        short temp;

        /* calculate gain_out with exponent */

        temp = shr(sig_out[0], 2);
        s = L_mult(temp, temp);
        for (i = 1; i < l_trm; i++)
        {
            temp = shr(sig_out[i], 2);
            s = L_mac(s, temp, temp);
        }

        if (s == 0)
        {
            return;
        }
        exp = sub(norm_l(s), 1);
        gain_out = round(L_shl(s, exp));

        /* calculate gain_in with exponent */

        temp = shr(sig_in[0], 2);
        s = L_mult(temp, temp);
        for (i = 1; i < l_trm; i++)
        {
            temp = shr(sig_in[i], 2);
            s = L_mac(s, temp, temp);
        }

        if (s == 0)
        {
            g0 = 0;
        }
        else
        {
            i = norm_l(s);
            gain_in = round(L_shl(s, i));
            exp = sub(exp, i);

            /*---------------------------------------------------*
             *  g0 = sqrt(gain_in/gain_out);                     *
             *---------------------------------------------------*/

            s = L_deposit_l(div_s(gain_out, gain_in));
            s = L_shl(s, 7);                   /* s = gain_out / gain_in */
            s = L_shr(s, exp);                 /* add exponent */

            s = Isqrt(s);
            g0 = round(L_shl(s, 9));
        }
        /* sig_out(n) = gain(n) sig_out(n) */

        for (i = 0; i < l_trm; i++)
        {
            sig_out[i] = extract_h(L_shl(L_mult(sig_out[i], g0), 2));
        }

        return;
    }

}
