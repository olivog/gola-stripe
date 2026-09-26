using System.Web.Mvc;

namespace GolaStripe.Controllers
{
    public class HomeController : Controller
    {
        // GET /  → solo llega aquí un dispositivo de confianza (DeviceGateFilter manda a los demás a PublicRedirectUrl)
        public ActionResult Index()
        {
            return RedirectToAction("New", "Payments");
        }
    }
}
