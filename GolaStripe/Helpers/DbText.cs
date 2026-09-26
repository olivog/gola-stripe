using System;
using System.Diagnostics;

namespace GolaStripe.Helpers
{
    /// <summary>
    /// Ajusta texto externo (Stripe / request) al tamaño de la columna antes de mandarlo a SQL.
    /// Los tamaños viven aquí para cambiarlos en un solo lugar si la DB cambia.
    /// </summary>
    public static class DbText
    {
        // dbo.Orders / dbo.WebhookEvents (script 07: IDs de Stripe varchar(255), nombres nvarchar(200))
        public const int StripeIdMax = 255;          // StripeSessionId, StripePaymentIntentId, StripeCustomerId, StripeEventId
        public const int CustomerNameMax = 200;      // Orders.CustomerName nvarchar(200)
        public const int ItemNameMax = 200;          // OrderItems.Name nvarchar(200)
        public const int CustomerEmailMax = 320;     // Orders.CustomerEmail varchar(320)
        public const int CountryMax = 2;             // Orders.CustomerCountry char(2)
        public const int PaymentMethodTypeMax = 40;  // Orders.PaymentMethodType varchar(40)
        public const int CardBrandMax = 40;          // Orders.CardBrand varchar(40)
        public const int CardLast4Max = 4;           // Orders.CardLast4 char(4)
        public const int ReceiptUrlMax = 500;        // Orders.ReceiptUrl varchar(500)
        public const int EventTypeMax = 100;         // WebhookEvents.EventType varchar(100)
        public const int ErrorMessageMax = 1000;     // WebhookEvents.ErrorMessage varchar(1000)
        public const int IpMax = 45;                 // TrustedDevices.LastIp / DeviceEnrollAttempts.Ip varchar(45)
        public const int UserAgentMax = 400;         // TrustedDevices.UserAgent nvarchar(400)
        public const int DeviceNameMax = 100;        // TrustedDevices.DeviceName nvarchar(100)

        /// <summary>
        /// Trim + corta a <paramref name="max"/> caracteres. null → null. Si corta, lo anota con Trace.
        /// No deja medio par sustituto (emoji) al final del corte.
        /// </summary>
        public static string Fit(string s, int max)
        {
            return Fit(s, max, null);
        }

        public static string Fit(string s, int max, string field)
        {
            if (s == null) return null;
            s = s.Trim();
            if (max <= 0 || s.Length <= max) return s;

            var cut = s.Substring(0, max);
            if (char.IsHighSurrogate(cut[cut.Length - 1]))
                cut = cut.Substring(0, cut.Length - 1);

            Trace.TraceWarning("DbText.Fit: " + (field ?? "valor") + " cortado de " + s.Length + " a " + cut.Length + " caracteres.");
            return cut;
        }

        /// <summary>Fit que devuelve DBNull.Value para null (para parámetros SQL anulables).</summary>
        public static object FitOrNull(string s, int max, string field)
        {
            var v = Fit(s, max, field);
            return v == null ? (object)DBNull.Value : v;
        }
    }
}
