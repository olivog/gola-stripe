using System.Web.Mvc;

namespace GolaStripe.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.Message = "Gola Stripe — OK";
            return View();
        }
    }
}
