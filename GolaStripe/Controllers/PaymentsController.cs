using System;
using System.Collections.Generic;
using System.Configuration;
using System.Web.Mvc;
using Stripe;
using Stripe.Checkout;
using GolaStripe.Helpers;
using GolaStripe.Resources;
using System.IO;
using System.Diagnostics;


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

        /// <summary>
        /// URL base para links/logo del email. En local (IIS Express) usa la URL del request para que
        /// los links funcionen; en el servidor usa PublicBaseUrl (https://checkout.golapr.com).
        /// </summary>
        private string GetPublicBaseUrl()
        {
            var fromRequest = Request.Url.GetLeftPart(UriPartial.Authority);
            if (Request.IsLocal)
                return fromRequest;

            var cfg = ConfigurationManager.AppSettings["PublicBaseUrl"];
            return string.IsNullOrWhiteSpace(cfg) ? fromRequest : cfg.Trim().TrimEnd('/');
        }

        /// <summary>Envía el recibo por email si la orden está Paid y aún no se envió. Nunca lanza.</summary>
        private string TrySendReceiptEmail(long orderId)
        {
            try
            {
                string mailError;
                ReceiptMailer.TrySendReceiptOnce(orderId, GetPublicBaseUrl(), out mailError);
                return mailError;
            }
            catch (Exception ex)
            {
                Trace.TraceError("Recibo email orden " + orderId + ": " + ex);
                return ex.Message;
            }
        }

        /// <summary>
        /// Success/Receipt: el idioma de la orden manda, salvo que el usuario haya pedido otro con ?lang=
        /// (prioridad: ?lang → Orders.Language → cookie/navegador, que ya aplicó LangFilter).
        /// </summary>
        private void ApplyOrderLanguage(GolaStripe.Models.ReceiptVm receipt)
        {
            if (receipt == null || Lang.FromQuery(Request) != null)
                return;
            var orderLang = Lang.Normalize(receipt.Language);
            if (orderLang != null)
                Lang.Apply(orderLang);
        }

        // GET /Payments/New
        [HttpGet]
        public ActionResult New()
        {
            ViewBag.ProductName = "";
            ViewBag.AmountDollars = "10.00";
            ViewBag.CustomerLang = Lang.Default; // idioma del cliente: inglés por defecto
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
        public ActionResult CreateCheckout(string productName, string amountDollars, string customerLang)
        {
            productName = (productName ?? "").Trim();
            customerLang = Lang.NormalizeOrDefault(customerLang); // "en" / "es", nunca null
            ViewBag.CustomerLang = customerLang;
            if (string.IsNullOrWhiteSpace(productName) || productName.Length > 200)
            {
                ViewBag.Error = Strings.Err_ProductName;
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
                ViewBag.Error = Strings.Err_Price;
                ViewBag.ProductName = productName;
                ViewBag.AmountDollars = amountDollars;
                return View("New");
            }

            // Stripe cobra en centavos: redondea a 2 decimales
            amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);

            EnsureStripeApiKey();

            const string currency = "usd";
            const int qty = 1;
            long orderId;
            Guid publicOrderId;
            Stripe.Checkout.Session session;
            try
            {
                var sourceAppId = GolaPayDb.GetSourceAppId("stripe");
                GolaPayDb.CreatePendingOrder(
                    sourceAppId, amount, currency, productName, qty, customerLang,
                    out orderId, out publicOrderId);
                int amountCents = Money.ToCents(amount);

                var domain = Request.Url.GetLeftPart(UriPartial.Authority);

                var options = new SessionCreateOptions
                {
                    Mode = "payment",
                    // Idioma del Checkout de Stripe (queda fijo para esta sesión)
                    Locale = customerLang,
                    // lang= lleva el idioma de la orden a Success/Cancel
                    SuccessUrl = domain + "/Payments/Success?lang=" + customerLang + "&session_id={CHECKOUT_SESSION_ID}",
                    CancelUrl = domain + "/Payments/Cancel?lang=" + customerLang + "&session_id={CHECKOUT_SESSION_ID}",
                    ClientReferenceId = publicOrderId.ToString(),
                    Metadata = new Dictionary<string, string>
            {
                { "order_id", orderId.ToString() },
                { "public_order_id", publicOrderId.ToString() },
                { "source_app", "stripe" },
                { "lang", customerLang }
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

                session = new SessionService().Create(options);
                GolaPayDb.SetStripeSessionId(orderId, session.Id);
            }
            catch (Exception ex)
            {
                Trace.TraceError("CreateCheckout: " + ex);
                ViewBag.Error = Strings.Err_CreateCheckout + (Request.IsLocal ? " (" + ex.Message + ")" : "");
                ViewBag.ProductName = productName;
                ViewBag.AmountDollars = amountDollars;
                return View("New");
            }

            // No redirigir al admin: mostrar link compartible para el cliente
            ViewBag.CheckoutUrl = session.Url;
            ViewBag.SessionId = session.Id;
            ViewBag.OrderId = orderId;
            ViewBag.PublicOrderId = publicOrderId;
            ViewBag.ProductName = productName;
            ViewBag.AmountDollars = amount;
            ViewBag.Currency = currency.ToUpperInvariant();
            ViewBag.CustomerLang = customerLang;
            return View("Link");
        }


        // GET /Payments/Success?session_id=cs_test_...
        public ActionResult Success(string session_id)
        {
            ViewBag.SessionId = session_id;

            GolaStripe.Models.ReceiptVm receipt = null;

            if (!string.IsNullOrEmpty(session_id))
            {
                try
                {
                    if (GolaPayDb.TryGetReceiptBySessionId(session_id, out receipt))
                    {
                        ApplyOrderLanguage(receipt);

                        // Respaldo del webhook: si ya está Paid y no se ha enviado el recibo, enviarlo.
                        // El claim en DB (ReceiptEmailSentAt) evita duplicados con el webhook.
                        if (receipt.IsPaid && !receipt.ReceiptEmailSentAtUtc.HasValue
                            && !string.IsNullOrWhiteSpace(receipt.CustomerEmail))
                        {
                            TrySendReceiptEmail(receipt.OrderId);
                        }
                    }
                    else
                    {
                        EnsureStripeApiKey();
                        var session = new SessionService().Get(
                            session_id,
                            new SessionGetOptions
                            {
                                Expand = new List<string> { "line_items" }
                            });

                        receipt = new GolaStripe.Models.ReceiptVm
                        {
                            StripeSessionId = session.Id,
                            Status = session.PaymentStatus,
                            Currency = session.Currency ?? "usd",
                            AmountTotal = (session.AmountTotal ?? 0) / 100m,
                            CustomerEmail = session.CustomerDetails != null
                                ? session.CustomerDetails.Email
                                : session.CustomerEmail,
                            CustomerName = session.CustomerDetails != null
                                ? session.CustomerDetails.Name
                                : null,
                            CustomerCountry = session.CustomerDetails != null
                                && session.CustomerDetails.Address != null
                                ? session.CustomerDetails.Address.Country
                                : null
                        };

                        if (session.LineItems != null && session.LineItems.Data != null)
                        {
                            foreach (var li in session.LineItems.Data)
                            {
                                var name = li.Description;
                                if (string.IsNullOrEmpty(name) && li.Price != null && li.Price.ProductId != null)
                                    name = li.Price.ProductId;

                                receipt.Lines.Add(new GolaStripe.Models.ReceiptLine
                                {
                                    Name = name ?? Strings.Item_Default,
                                    Quantity = (int)(li.Quantity ?? 1),
                                    UnitAmount = (li.Price != null && li.Price.UnitAmount != null
                                        ? li.Price.UnitAmount.Value
                                        : (li.AmountTotal / Math.Max(1, li.Quantity ?? 1))) / 100m,
                                    Currency = li.Currency ?? receipt.Currency
                                });
                            }
                        }

                        if (receipt.Lines.Count == 0 && receipt.AmountTotal > 0)
                        {
                            receipt.Lines.Add(new GolaStripe.Models.ReceiptLine
                            {
                                Name = Strings.Item_Payment,
                                Quantity = 1,
                                UnitAmount = receipt.AmountTotal,
                                Currency = receipt.Currency
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Success " + session_id + ": " + ex);
                    ViewBag.Error = Strings.Receipt_LoadError;
                    ViewBag.ErrorDetail = Request.IsLocal ? ex.Message : null;
                }
            }

            return View(receipt);
        }

        // GET /Payments/Receipt?id={PublicOrderId}  (link del email)
        [HttpGet]
        public ActionResult Receipt(string id)
        {
            GolaStripe.Models.ReceiptVm receipt = null;
            Guid publicId;

            if (Guid.TryParse((id ?? "").Trim(), out publicId))
            {
                try
                {
                    if (!GolaPayDb.TryGetReceiptByPublicId(publicId, out receipt) || !receipt.IsPaid)
                        receipt = null;
                    else
                        ApplyOrderLanguage(receipt);
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Receipt " + id + ": " + ex);
                    ViewBag.Error = Strings.Receipt_LoadErrorTryLater;
                    receipt = null;
                }
            }

            if (receipt == null)
                Response.StatusCode = 404;

            Response.TrySkipIisCustomErrors = true;
            return View(receipt);
        }

        // GET /Payments/Cancel?session_id=cs_test_...
        public ActionResult Cancel(string session_id)
        {
            ViewBag.SessionId = session_id;
            ViewBag.OrderStatus = null;
            ViewBag.OrderId = null;

            if (!string.IsNullOrEmpty(session_id))
            {
                try
                {
                    long? orderId;
                    string status;
                    var changed = GolaPayDb.TryMarkOrderCancelledBySessionId(session_id, out orderId, out status);
                    ViewBag.OrderId = orderId;
                    ViewBag.OrderStatus = status;
                    ViewBag.JustCancelled = changed;
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Cancel " + session_id + ": " + ex);
                    ViewBag.Error = Strings.Cancel_Error;
                    ViewBag.ErrorDetail = Request.IsLocal ? ex.Message : null;
                }
            }

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
                        EnsureStripeApiKey();
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

                        if (cardLast4 == null && !string.IsNullOrEmpty(pi.PaymentMethodId))
                        {
                            var pm = new PaymentMethodService().Get(pi.PaymentMethodId);
                            if (pm.Card != null)
                            {
                                cardBrand = pm.Card.Brand;
                                cardLast4 = pm.Card.Last4;
                            }
                        }
                        if (cardLast4 == null)
                        {
                            var chargeId = pi.LatestChargeId;
                            if (string.IsNullOrEmpty(chargeId) && pi.LatestCharge != null)
                                chargeId = pi.LatestCharge.Id;

                            if (!string.IsNullOrEmpty(chargeId))
                            {
                                var charge = new ChargeService().Get(chargeId);
                                if (charge.PaymentMethodDetails != null
                                    && charge.PaymentMethodDetails.Card != null)
                                {
                                    cardBrand = charge.PaymentMethodDetails.Card.Brand;
                                    cardLast4 = charge.PaymentMethodDetails.Card.Last4;
                                }
                                if (string.IsNullOrEmpty(pmType) && charge.PaymentMethodDetails != null)
                                    pmType = charge.PaymentMethodDetails.Type;
                            }
                        }
                    }
                    string customerCountry = null;
                    if (session.CustomerDetails != null
                        && session.CustomerDetails.Address != null)
                    {
                        customerCountry = session.CustomerDetails.Address.Country; // "PR", "US", …
                    }
                    GolaPayDb.MarkOrderPaidFromCheckoutSession(
                        orderId.Value,
                        session.PaymentIntentId,
                        session.CustomerEmail ?? (session.CustomerDetails != null ? session.CustomerDetails.Email : null),
                        session.CustomerDetails != null ? session.CustomerDetails.Name : null,
                        customerCountry,
                        session.CustomerId,
                        pmType,
                        cardBrand,
                        cardLast4,
                        null,
                        amountDollars);

                    // Recibo por email (una sola vez por orden, aunque Stripe reenvíe el evento).
                    // Si falla no rompe el webhook: se anota en WebhookEvents.ErrorMessage y se devuelve 200.
                    var mailError = TrySendReceiptEmail(orderId.Value);
                    if (mailError != null && mailError.Length > 900)
                        mailError = mailError.Substring(0, 900);

                    GolaPayDb.CompleteWebhookEvent(webhookEventId, orderId, "Processed",
                        mailError == null ? null : "Recibo email: " + mailError);
                }
                                else if (stripeEvent.Type == "checkout.session.expired")
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

                    GolaPayDb.TryMarkOrderExpired(orderId.Value);
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