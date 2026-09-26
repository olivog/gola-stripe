using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Mvc;
using GolaStripe.Models;

namespace GolaStripe.Helpers
{
    /// <summary>
    /// Acceso admin por dispositivo de confianza (sin login).
    /// Cookie gola_device = 32 bytes aleatorios en base64url; en la DB solo se guarda su SHA-256 (32 bytes).
    /// </summary>
    public static class DeviceAuth
    {
        public const string CookieName = "gola_device";
        private const string ItemsKey = "gola_device";
        private const string AdminItemsKey = "gola_admin_request";
        private const int CookieYears = 2;

        /// <summary>A dónde se manda a cualquiera que no sea un dispositivo de confianza.</summary>
        public static string PublicRedirectUrl
        {
            get
            {
                var v = ConfigurationManager.AppSettings["PublicRedirectUrl"];
                return string.IsNullOrWhiteSpace(v) ? "https://golapr.com" : v.Trim();
            }
        }

        /// <summary>Clave de alta (appSettings DeviceEnrollKey, en Web.secrets.config). null si falta o es corta (&lt; 16).</summary>
        public static string EnrollKey
        {
            get
            {
                var v = ConfigurationManager.AppSettings["DeviceEnrollKey"];
                v = v == null ? null : v.Trim();
                return string.IsNullOrEmpty(v) || v.Length < 16 ? null : v;
            }
        }

        /// <summary>Dispositivo validado en este request (o null).</summary>
        public static TrustedDevice Current
        {
            get
            {
                var ctx = HttpContext.Current;
                return ctx == null ? null : ctx.Items[ItemsKey] as TrustedDevice;
            }
        }

        /// <summary>true si el request es de una página admin (acción sin [PublicAccess]) → mostrar barra admin.</summary>
        public static bool IsAdminRequest
        {
            get
            {
                var ctx = HttpContext.Current;
                return ctx != null && ctx.Items[AdminItemsKey] is bool && (bool)ctx.Items[AdminItemsKey];
            }
        }

        internal static void SetCurrent(HttpContextBase ctx, TrustedDevice device, bool adminRequest)
        {
            ctx.Items[ItemsKey] = device;
            ctx.Items[AdminItemsKey] = adminRequest;
        }

        // ---------- Token ----------

        /// <summary>Nuevo token: 32 bytes de RNGCryptoServiceProvider.</summary>
        public static byte[] NewToken()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            return bytes;
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        public static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Decodifica base64url; null si no es válido.</summary>
        public static byte[] Base64UrlDecode(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 100) return null;
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return null;
            }
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return null; }
        }

        /// <summary>SHA-256 del token de la cookie (32 bytes) o null si no hay cookie válida.</summary>
        public static byte[] TokenHashFromRequest(HttpRequestBase request)
        {
            var c = request.Cookies[CookieName];
            if (c == null) return null;
            var raw = Base64UrlDecode(c.Value);
            if (raw == null || raw.Length != 32) return null;
            return Sha256(raw);
        }

        /// <summary>Busca el dispositivo de la cookie. Nunca lanza (si la DB falla, se trata como no confiable).</summary>
        public static TrustedDevice FindFromRequest(HttpRequestBase request)
        {
            try
            {
                var hash = TokenHashFromRequest(request);
                return hash == null ? null : DeviceDb.FindActiveByHash(hash);
            }
            catch (Exception ex)
            {
                Trace.TraceError("DeviceAuth: " + ex);
                return null;
            }
        }

        /// <summary>Comparación en tiempo constante (compara SHA-256 de ambos, no filtra el largo).</summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            var ha = Sha256(Encoding.UTF8.GetBytes(a ?? ""));
            var hb = Sha256(Encoding.UTF8.GetBytes(b ?? ""));
            var diff = 0;
            for (var i = 0; i < ha.Length; i++)
                diff |= ha[i] ^ hb[i];
            return diff == 0 && a != null && b != null;
        }

        // ---------- Cookie ----------
        // Se escribe el header a mano para poder poner SameSite=Lax (HttpCookie.SameSite no existe
        // en las reference assemblies de .NET 4.7.2).

        public static void SetCookie(HttpRequestBase request, HttpResponseBase response, string tokenValue)
        {
            WriteCookie(request, response, tokenValue, DateTime.UtcNow.AddYears(CookieYears));
        }

        public static void ClearCookie(HttpRequestBase request, HttpResponseBase response)
        {
            WriteCookie(request, response, "", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        /// <summary>Renueva el vencimiento (2 años desde hoy) con el mismo valor de la cookie actual.</summary>
        public static void SlideCookie(HttpRequestBase request, HttpResponseBase response)
        {
            var c = request.Cookies[CookieName];
            if (c != null && !string.IsNullOrEmpty(c.Value))
                SetCookie(request, response, c.Value);
        }

        private static void WriteCookie(HttpRequestBase request, HttpResponseBase response, string value, DateTime expiresUtc)
        {
            var sb = new StringBuilder();
            sb.Append(CookieName).Append('=').Append(value ?? "");
            sb.Append("; expires=").Append(expiresUtc.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("; path=/; HttpOnly; SameSite=Lax");
            if (request.IsSecureConnection)
                sb.Append("; Secure");
            response.AppendHeader("Set-Cookie", sb.ToString());
        }
    }

    /// <summary>
    /// Filtro global "default deny": pasa si la acción/controlador tiene [PublicAccess]
    /// o si la cookie gola_device corresponde a un TrustedDevices activo. Si no → 302 a PublicRedirectUrl.
    /// </summary>
    public class DeviceGateFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationContext filterContext)
        {
            if (filterContext.IsChildAction) return;

            var http = filterContext.HttpContext;
            var ad = filterContext.ActionDescriptor;
            var isPublic = ad.IsDefined(typeof(PublicAccessAttribute), true)
                || ad.ControllerDescriptor.IsDefined(typeof(PublicAccessAttribute), true);

            // En páginas públicas solo se identifica el dispositivo si trae cookie (p. ej. Enroll: "ya registrado").
            var device = http.Request.Cookies[DeviceAuth.CookieName] != null
                ? DeviceAuth.FindFromRequest(http.Request)
                : null;

            if (device == null && !isPublic)
            {
                filterContext.Result = new RedirectResult(DeviceAuth.PublicRedirectUrl);
                return;
            }

            if (device != null)
            {
                // Como mucho 1 escritura cada 10 min: LastSeenAt/LastIp/UserAgent + renovar cookie.
                if (!device.LastSeenAtUtc.HasValue
                    || device.LastSeenAtUtc.Value < DateTime.UtcNow.AddMinutes(-10))
                {
                    try
                    {
                        if (DeviceDb.TouchDevice(device.DeviceId, http.Request.UserHostAddress, http.Request.UserAgent))
                            DeviceAuth.SlideCookie(http.Request, http.Response);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError("DeviceGate touch " + device.DeviceId + ": " + ex);
                    }
                }
            }

            DeviceAuth.SetCurrent(http, device, !isPublic);
        }
    }
}
