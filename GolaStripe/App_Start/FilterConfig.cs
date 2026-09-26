using System.Web.Mvc;

namespace GolaStripe
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new HandleErrorAttribute());
            // Acceso admin: default deny. Solo [PublicAccess] o cookie gola_device válida (ver Helpers\DeviceAuth.cs)
            filters.Add(new GolaStripe.Helpers.DeviceGateFilter());
            // Idioma por request: ?lang → cookie gola_lang → Accept-Language → en (ver Helpers\Lang.cs)
            filters.Add(new GolaStripe.Helpers.LangFilter());
        }
    }
}
