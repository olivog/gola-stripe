using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Web;
using System.Web.Mvc;
using GolaStripe.Resources;

namespace GolaStripe.Helpers
{
    /// <summary>
    /// Idioma de la UI. Para agregar un idioma: añadirlo a Supported + CultureFor/DisplayName
    /// y crear Resources\Strings.xx.resx. Lo que falte en un .resx cae al inglés (Strings.resx).
    /// Prioridad (LangFilter): ?lang=xx (y cookie gola_lang 1 año) → cookie → Accept-Language → "en".
    /// Success/Receipt: si no vino ?lang, se usa el idioma de la orden (Orders.Language).
    /// </summary>
    public static class Lang
    {
        public const string Default = "en";
        public const string QueryKey = "lang";
        public const string CookieName = "gola_lang";

        /// <summary>Códigos soportados (minúsculas, como se guardan en Orders.Language).</summary>
        public static readonly string[] Supported = { "en", "es" };

        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private static readonly CultureInfo Es = FindCulture("es-PR", "es-419", "es");

        /// <summary>Código actual ("en"/"es") según CurrentUICulture.</summary>
        public static string Current
        {
            get { return Normalize(Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName) ?? Default; }
        }

        /// <summary>"en"/"es" si es soportado (acepta "es-PR", "EN", etc.); si no, null.</summary>
        public static string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            code = code.Trim().ToLowerInvariant();
            var dash = code.IndexOfAny(new[] { '-', '_' });
            if (dash > 0) code = code.Substring(0, dash);
            return Supported.Contains(code) ? code : null;
        }

        public static string NormalizeOrDefault(string code)
        {
            return Normalize(code) ?? Default;
        }

        public static CultureInfo CultureFor(string code)
        {
            switch (NormalizeOrDefault(code))
            {
                case "es": return Es;
                default: return EnUs;
            }
        }

        /// <summary>Nombre del idioma en su propio idioma (para selectores).</summary>
        public static string DisplayName(string code)
        {
            switch (NormalizeOrDefault(code))
            {
                case "es": return "Español";
                default: return "English";
            }
        }

        /// <summary>Pone CurrentCulture y CurrentUICulture del hilo actual.</summary>
        public static void Apply(string code)
        {
            var c = CultureFor(code);
            Thread.CurrentThread.CurrentCulture = c;
            Thread.CurrentThread.CurrentUICulture = c;
        }

        /// <summary>Idioma pedido explícitamente con ?lang= (null si no vino o no es válido).</summary>
        public static string FromQuery(HttpRequestBase request)
        {
            return request == null ? null : Normalize(request.QueryString[QueryKey]);
        }

        /// <summary>Resuelve el idioma del request y guarda la cookie si vino ?lang=.</summary>
        public static string Resolve(HttpRequestBase request, HttpResponseBase response)
        {
            var q = FromQuery(request);
            if (q != null)
            {
                if (response != null)
                {
                    var cookie = new HttpCookie(CookieName, q)
                    {
                        Expires = DateTime.UtcNow.AddYears(1),
                        HttpOnly = true,
                        Path = "/",
                        Secure = request.IsSecureConnection
                    };
                    response.Cookies.Set(cookie);
                }
                return q;
            }

            var ck = request.Cookies[CookieName];
            var fromCookie = ck == null ? null : Normalize(ck.Value);
            if (fromCookie != null)
                return fromCookie;

            if (request.UserLanguages != null)
            {
                foreach (var ul in request.UserLanguages)
                {
                    // "es-PR;q=0.9" → "es-PR"
                    var tag = (ul ?? "").Split(';')[0];
                    var n = Normalize(tag);
                    if (n != null) return n;
                }
            }
            return Default;
        }

        /// <summary>Misma URL (path + query) con lang reemplazado; conserva los demás parámetros.</summary>
        public static string SwitchUrl(HttpRequestBase request, string code)
        {
            var qs = HttpUtility.ParseQueryString(request.Url.Query);
            qs.Remove(QueryKey);
            var parts = new List<string>();
            foreach (string key in qs.AllKeys)
            {
                if (key == null) continue;
                foreach (var v in qs.GetValues(key) ?? new string[0])
                    parts.Add(HttpUtility.UrlEncode(key) + "=" + HttpUtility.UrlEncode(v));
            }
            parts.Add(QueryKey + "=" + HttpUtility.UrlEncode(NormalizeOrDefault(code)));
            return request.Url.AbsolutePath + "?" + string.Join("&", parts);
        }

        /// <summary>Texto de recursos en una cultura específica (email desde el webhook). Nunca null.</summary>
        public static string Get(string key, CultureInfo culture)
        {
            return Strings.ResourceManager.GetString(key, culture ?? Thread.CurrentThread.CurrentUICulture) ?? key;
        }

        /// <summary>Fecha UTC → AST (UTC-4, sin horario de verano), formato corto de la cultura + " AST".</summary>
        public static string FormatAst(DateTime utc, CultureInfo culture)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime local;
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("SA Western Standard Time");
                local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            }
            catch (Exception)
            {
                local = utc.AddHours(-4);
            }
            return local.ToString("g", culture ?? Thread.CurrentThread.CurrentCulture) + " AST";
        }

        /// <summary>Etiqueta traducida del estado de la orden (Paid/Pending/...) o de Stripe (paid/unpaid).</summary>
        public static string StatusLabel(string status, CultureInfo culture = null)
        {
            if (string.IsNullOrEmpty(status)) return "-";
            switch (status.Trim().ToLowerInvariant())
            {
                case "paid": return Get("Status_Paid", culture);
                case "pending": return Get("Status_Pending", culture);
                case "cancelled":
                case "canceled": return Get("Status_Cancelled", culture);
                case "expired": return Get("Status_Expired", culture);
                case "unpaid": return Get("Status_Unpaid", culture);
                default: return status;
            }
        }

        private static CultureInfo FindCulture(params string[] names)
        {
            foreach (var n in names)
            {
                try { return CultureInfo.GetCultureInfo(n); }
                catch (CultureNotFoundException) { }
            }
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Filtro global: fija la cultura de cada request (ver Lang.Resolve).</summary>
    public class LangFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext filterContext)
        {
            if (filterContext.IsChildAction) return;
            var http = filterContext.HttpContext;
            Lang.Apply(Lang.Resolve(http.Request, http.Response));
        }

        public void OnActionExecuted(ActionExecutedContext filterContext)
        {
        }
    }
}
