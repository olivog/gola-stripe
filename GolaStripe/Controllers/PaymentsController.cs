using System;
using System.Collections.Generic;
using System.Configuration;
using System.Web.Mvc;
using Stripe;
using Stripe.Checkout;
using GolaStripe.Helpers;

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

            const decimal amountDollars = 10.00m; // o 10.50m
            const string currency = "usd";
            const string itemName = "Prueba Gola Stripe";
            const int qty = 1;
            var sourceAppId = GolaPayDb.GetSourceAppId("stripe");
            long orderId;
            Guid publicOrderId;
            GolaPayDb.CreatePendingOrder(
                sourceAppId, amountDollars, currency, itemName, qty,
                out orderId, out publicOrderId);
            int amountCents = Money.ToCents(amountDollars);

            // 2) Checkout Session con metadata para el webhook (paso 3)
            var domain = Request.Url.GetLeftPart(UriPartial.Authority);

            var options = new SessionCreateOptions
            {
                Mode = "payment",
                SuccessUrl = domain + "/Payments/Success?session_id={CHECKOUT_SESSION_ID}",
                CancelUrl = domain + "/Payments/Cancel",
                ClientReferenceId = publicOrderId.ToString(),
                Metadata = new Dictionary<string, string>
        {
            { "order_id", orderId.ToString() },
            { "public_order_id", publicOrderId.ToString() },
            { "source_app", "stripe" }
        },
                LineItems = new List<SessionLineItemOptions>
        {
            new SessionLineItemOptions
            {
                Quantity = qty,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = currency,
                    UnitAmount = amountCents,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = itemName
                    }
                }
            }
        }
            };

            var session = new SessionService().Create(options);

            // 3) Guardar cs_... en la orden
            GolaPayDb.SetStripeSessionId(orderId, session.Id);

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