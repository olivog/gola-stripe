using System;
using System.Collections.Generic;
using System.Configuration;
using System.Web.Mvc;
using Stripe;
using Stripe.Checkout;

namespace GolaStripe.Controllers
{
    public class PaymentsController : Controller
    {
        private static void EnsureStripeApiKey()
        {
            var sk = ConfigurationManager.AppSettings["StripeSecretKey"];
            if (string.IsNullOrWhiteSpace(sk))
            {
                throw new InvalidOperationException(
                    "Falta StripeSecretKey. Copia Web.secrets.config.example a Web.secrets.config y pega tus keys de test.");
            }
            StripeConfiguration.ApiKey = sk;
        }

        // GET /Payments/CreateCheckout
        public ActionResult CreateCheckout()
        {
            EnsureStripeApiKey();

            var domain = Request.Url.GetLeftPart(UriPartial.Authority);

            var options = new SessionCreateOptions
            {
                Mode = "payment",
                SuccessUrl = domain + "/Payments/Success?session_id={CHECKOUT_SESSION_ID}",
                CancelUrl = domain + "/Payments/Cancel",
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        Quantity = 1,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = "usd",
                            UnitAmount = 1000, // $10.00
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = "Prueba Gola Stripe"
                            }
                        }
                    }
                }
            };

            var service = new SessionService();
            Session session = service.Create(options);

            return Redirect(session.Url);
        }

        // GET /Payments/Success?session_id=cs_test_...
        public ActionResult Success(string session_id)
        {
            ViewBag.SessionId = session_id;

            if (!string.IsNullOrEmpty(session_id))
            {
                try
                {
                    EnsureStripeApiKey();
                    var session = new SessionService().Get(session_id);
                    ViewBag.PaymentStatus = session.PaymentStatus;
                    ViewBag.AmountTotal = session.AmountTotal; // centavos
                }
                catch (Exception ex)
                {
                    ViewBag.Error = ex.Message;
                }
            }

            return View();
        }

        // GET /Payments/Cancel
        public ActionResult Cancel()
        {
            return View();
        }
    }
}