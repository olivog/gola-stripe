using System;

namespace GolaStripe.Helpers
{
    public static class Money
    {
        public static int ToCents(decimal dollars)
        {
            return (int)Math.Round(dollars * 100m, MidpointRounding.AwayFromZero);
        }

        public static decimal FromCents(int cents)
        {
            return cents / 100m;
        }
    }
}