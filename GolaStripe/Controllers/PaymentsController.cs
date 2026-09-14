using System;
using System.Collections.Generic;
using System.Configuration;
using System.Web.Mvc;
using Stripe;
using Stripe.Checkout;
using GolaStripe.Helpers;
using System.IO;


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

        // GET /Payments/New
        [HttpGet]
        public ActionResult New()
        {
            ViewBag.ProductName = "";
            ViewBag.AmountDollars = "10.00";
            return View();
        }

        // GET viejo → manda al form
        [HttpGet]
        public ActionResult CreateCheckout()
        {
            return RedirectToAction("New");
        }

        // POST /Payments/CreateCheckout
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult CreateCheckout(string productName, string amountDollars)
        {
            productName = (productName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(productName) || productName.Length > 200)
            {
                ViewBag.Error = "Escribe un nombre de producto (máx. 200).";
                ViewBag.ProductName = productName;
                ViewBag.AmountDollars = amountDollars;
                return View("New");
            }

            decimal amount;
            if (!decimal.TryParse(
                    (amountDollars ?? "").Trim().Replace(',', '.'),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out amount)
                || amount < 0.50m
                || amount > 99999.99m)
            {
                ViewBag.Error = "Precio inválido. Usa entre 0.50 y 99999.99 USD.";
                ViewBag.ProductName = productName;
                ViewBag.AmountDollars = amountDollars;
                return View("New");
            }

            // Stripe cobra en centavos: redondea a 2 decimales
            amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);

            EnsureStripeApiKey();

            const string currency = "usd";
            const int qty = 1;
            var sourceAppId = GolaPayDb.GetSourceAppId("stripe");
            long orderId;
            Guid publicOrderId;
            GolaPayDb.CreatePendingOrder(
                sourceAppId, amount, currency, productName, qty,
                out orderId, out publicOrderId);
            int amountCents = Money.ToCents(amount);

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
                        Name = productName
                    }
                }
            }
        }
            };

            var session = new SessionService().Create(options);
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
        [HttpPost]
        public ActionResult StripeWebhook()
        {
            var json = new StreamReader(Request.InputStream).ReadToEnd();
            var signature = Request.Headers["Stripe-Signature"];
            var whsec = ConfigurationManager.AppSettings["StripeWebhookSecret"];

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json, signature, whsec, tolerance: 300, throwOnApiVersionMismatch: false);
            }
            catch (Exception)
            {
                return new HttpStatusCodeResult(400);
            }

            long webhookEventId;
            var isNew = GolaPayDb.TryBeginWebhookEvent(
                stripeEvent.Id,
                stripeEvent.Type,
                stripeEvent.Livemode,
                json,
                out webhookEventId);

            if (!isNew)
                return new HttpStatusCodeResult(200);

            try
            {
                if (stripeEvent.Type == "checkout.session.completed")//Events.CheckoutSessionCompleted)
                {
                    var session = stripeEvent.Data.Object as Session;
                    long? orderId = null;

                    if (session.Metadata != null && session.Metadata.ContainsKey("order_id"))
                        orderId = long.Parse(session.Metadata["order_id"]);
                    else if (!string.IsNullOrEmpty(session.ClientReferenceId))
                        orderId = GolaPayDb.FindOrderIdByPublicOrderId(Guid.Parse(session.ClientReferenceId));
                    else
                        orderId = GolaPayDb.FindOrderIdBySessionId(session.Id);

                    if (orderId == null)
                    {
                        GolaPayDb.CompleteWebhookEvent(webhookEventId, null, "Failed", "Order not found");
                        return new HttpStatusCodeResult(200);
                    }

                    var amountDollars = Money.FromCents((int)(session.AmountTotal ?? 0));

                    string pmType = null;
                    if (session.PaymentMethodTypes != null && session.PaymentMethodTypes.Count > 0)
                        pmType = session.PaymentMethodTypes[0];

                    string cardBrand = null;
                    string cardLast4 = null;

                    if (!string.IsNullOrEmpty(session.PaymentIntentId))
                    {
                        var pi = new PaymentIntentService().Get(
                            session.PaymentIntentId,
                            new PaymentIntentGetOptions
                            {
                                Expand = new List<string> { "payment_method", "latest_charge" }
                            });

                        if (pi.PaymentMethod != null && pi.PaymentMethod.Card != null)
                        {
                            cardBrand = pi.PaymentMethod.Card.Brand;
                            cardLast4 = pi.PaymentMethod.Card.Last4;
                        }
                        else if (pi.LatestCharge != null
                                 && pi.LatestCharge.PaymentMethodDetails != null
                                 && pi.LatestCharge.PaymentMethodDetails.Card != null)
                        {
                            cardBrand = pi.LatestCharge.PaymentMethodDetails.Card.Brand;
                            cardLast4 = pi.LatestCharge.PaymentMethodDetails.Card.Last4;
                        }

                        if (string.IsNullOrEmpty(pmType) && pi.PaymentMethod != null)
                            pmType = pi.PaymentMethod.Type;
                    }

                    GolaPayDb.MarkOrderPaidFromCheckoutSession(
                        orderId.Value,
                        session.PaymentIntentId,
                        session.CustomerEmail ?? (session.CustomerDetails != null ? session.CustomerDetails.Email : null),
                        session.CustomerDetails != null ? session.CustomerDetails.Name : null,
                        session.CustomerId,
                        pmType,
                        cardBrand,
                        cardLast4,
                        null,
                        amountDollars);

                    GolaPayDb.CompleteWebhookEvent(webhookEventId, orderId, "Processed", null);
                }
                else
                {
                    GolaPayDb.CompleteWebhookEvent(webhookEventId, null, "Ignored", null);
                }
            }
            catch (Exception ex)
            {
                GolaPayDb.CompleteWebhookEvent(webhookEventId, null, "Failed", ex.Message);
            }

            return new HttpStatusCodeResult(200);
        }
    }
}