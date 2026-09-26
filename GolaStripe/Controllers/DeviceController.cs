using System;
using System.Diagnostics;
using System.Web.Mvc;
using GolaStripe.Helpers;

namespace GolaStripe.Controllers
{
    /// <summary>
    /// /Device/Enroll (público, oculto): alta de un dispositivo de confianza con la clave DeviceEnrollKey.
    /// /Device (admin): lista, revocar, "olvidar este dispositivo". Solo inglés (admin).
    /// </summary>
    public class DeviceController : Controller
    {
        private const int MaxFailedAttempts = 5; // por IP en 15 minutos

        // GET /Device
        [HttpGet]
        public ActionResult Index()
        {
            ViewBag.Title = "Devices";
            ViewBag.Message = Request.QueryString["revoked"] == "1" ? "Device revoked." : null;
            return View(DeviceDb.ListDevices());
        }

        // POST /Device/Revoke
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Revoke(int id)
        {
            var current = DeviceAuth.Current;
            DeviceDb.RevokeDevice(id);

            if (current != null && current.DeviceId == id)
            {
                DeviceAuth.ClearCookie(Request, Response);
                return Redirect(DeviceAuth.PublicRedirectUrl);
            }

            return RedirectToAction("Index", new { revoked = 1 });
        }

        // POST /Device/Forget  (borra la cookie y revoca este dispositivo)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Forget()
        {
            var current = DeviceAuth.Current;
            if (current != null)
                DeviceDb.RevokeDevice(current.DeviceId);
            DeviceAuth.ClearCookie(Request, Response);
            return Redirect(DeviceAuth.PublicRedirectUrl);
        }

        // GET /Device/Enroll
        [HttpGet]
        [PublicAccess]
        public ActionResult Enroll()
        {
            PrepareEnrollView(null, null);
            return View();
        }

        // POST /Device/Enroll
        [HttpPost]
        [PublicAccess]
        [ValidateAntiForgeryToken]
        [ValidateInput(false)] // permite "<" en deviceName; se muestra HTML-encoded
        public ActionResult Enroll(string enrollKey, string deviceName)
        {
            deviceName = (deviceName ?? "").Trim();
            var ip = Request.UserHostAddress ?? "";

            var existing = DeviceAuth.Current;
            if (existing != null)
            {
                PrepareEnrollView(null, deviceName);
                return View();
            }

            var key = DeviceAuth.EnrollKey;
            if (key == null)
            {
                PrepareEnrollView(null, deviceName);
                return View();
            }

            try { DeviceDb.PurgeOldEnrollAttempts(); }
            catch (Exception ex) { Trace.TraceError("PurgeOldEnrollAttempts: " + ex); }

            if (DeviceDb.CountRecentFailedAttempts(ip) >= MaxFailedAttempts)
            {
                DeviceDb.LogEnrollAttempt(ip, false);
                PrepareEnrollView("Too many attempts. Please try again later.", deviceName);
                return View();
            }

            if (!DeviceAuth.FixedTimeEquals(enrollKey ?? "", key))
            {
                DeviceDb.LogEnrollAttempt(ip, false);
                PrepareEnrollView("Invalid enrollment key.", deviceName);
                return View();
            }

            if (deviceName.Length == 0 || deviceName.Length > 100)
            {
                // La clave era correcta: no cuenta como intento fallido, pero se registra.
                DeviceDb.LogEnrollAttempt(ip, true);
                PrepareEnrollView("Enter a device name (max. 100 characters).", deviceName);
                return View();
            }

            var token = DeviceAuth.NewToken();
            DeviceDb.InsertDevice(deviceName, DeviceAuth.Sha256(token), ip, Request.UserAgent);
            DeviceDb.LogEnrollAttempt(ip, true);
            DeviceAuth.SetCookie(Request, Response, DeviceAuth.Base64UrlEncode(token));
            return RedirectToAction("New", "Payments");
        }

        private void PrepareEnrollView(string error, string deviceName)
        {
            ViewBag.Title = "Enroll device";
            ViewBag.NoIndex = true;
            ViewBag.HideLangSwitch = true;
            ViewBag.Error = error;
            ViewBag.DeviceName = deviceName;
            ViewBag.EnrollDisabled = DeviceAuth.EnrollKey == null;
            ViewBag.ExistingDevice = DeviceAuth.Current;
        }
    }
}
