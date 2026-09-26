using System;
using System.Globalization;

namespace GolaStripe.Helpers
{
    public static class Money
    {
        private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

        /// <summary>Monto en USD siempre con formato fijo en-US ("$3,500.00"), sin importar el idioma.</summary>
        public static string Format(decimal dollars)
        {
            return dollars.ToString("C", Usd);
        }

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