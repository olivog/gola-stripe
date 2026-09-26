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
using System.Globalization;


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
        [ValidateInput(false)] // permite "<" en productName; se muestra siempre HTML-encoded (Razor / HtmlEncode en el email)
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
            long orderId = 0;
            Guid publicOrderId = Guid.Empty;
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
                GolaPayDb.SetStripeSessionId(orderId, session.Id, session.Livemode);
            }
            catch (Exception ex)
            {
                Trace.TraceError("CreateCheckout: " + ex);

                // La orden se insertó Pending con StripeSessionId NULL antes de llamar a Stripe:
                // si algo falló después, no dejarla Pending para siempre.
                if (orderId > 0)
                {
                    try { GolaPayDb.TryMarkOrderExpired(orderId); }
                    catch (Exception ex2) { Trace.TraceError("CreateCheckout: no se pudo marcar Expired la orden " + orderId + ": " + ex2); }
                }

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


        // GET /Payments/Received?range=today|7d|month|all&from=yyyy-MM-dd&to=yyyy-MM-dd&q=...&page=N
        // Admin (protegido por DeviceGateFilter), solo lectura. Fechas del filtro en AST → límites UTC.
        [HttpGet]
        [ValidateInput(false)] // permite "<" en q (búsqueda); se muestra HTML-encoded
        public ActionResult Received(string range, string from, string to, string q, int? page)
        {
            var vm = new GolaStripe.Models.ReceivedPageVm();
            vm.Q = (q ?? "").Trim();
            if (vm.Q.Length > 100) vm.Q = vm.Q.Substring(0, 100);
            vm.Page = page.HasValue && page.Value > 0 ? page.Value : 1;

            var today = Ast.TodayAst;
            DateTime fromAst, toAst;
            var hasFrom = TryParseDay(from, out fromAst);
            var hasTo = TryParseDay(to, out toAst);

            if (hasFrom || hasTo)
            {
                // Rango personalizado (días AST, "to" incluido)
                if (hasFrom && hasTo && fromAst > toAst)
                {
                    var t = fromAst; fromAst = toAst; toAst = t;
                }
                vm.Range = "custom";
                vm.From = hasFrom ? fromAst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                vm.To = hasTo ? toAst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                vm.FromUtc = hasFrom ? Ast.AstToUtc(fromAst) : (DateTime?)null;
                vm.ToUtc = hasTo ? Ast.AstToUtc(toAst.AddDays(1)) : (DateTime?)null;
                vm.RangeLabel = (hasFrom ? fromAst.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "…")
                    + " – " + (hasTo ? toAst.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "…");
            }
            else
            {
                if ((!string.IsNullOrWhiteSpace(from) && !hasFrom) || (!string.IsNullOrWhiteSpace(to) && !hasTo))
                    vm.Error = "Invalid date. Use the date picker (yyyy-MM-dd).";

                switch ((range ?? "month").Trim().ToLowerInvariant())
                {
                    case "today":
                        vm.Range = "today";
                        vm.FromUtc = Ast.AstToUtc(today);
                        vm.ToUtc = Ast.AstToUtc(today.AddDays(1));
                        vm.RangeLabel = "Today";
                        break;
                    case "7d":
                        vm.Range = "7d";
                        vm.FromUtc = Ast.AstToUtc(today.AddDays(-6));
                        vm.ToUtc = Ast.AstToUtc(today.AddDays(1));
                        vm.RangeLabel = "Last 7 days";
                        break;
                    case "all":
                        vm.Range = "all";
                        vm.RangeLabel = "All time";
                        break;
                    default:
                        var first = new DateTime(today.Year, today.Month, 1);
                        vm.Range = "month";
                        vm.FromUtc = Ast.AstToUtc(first);
                        vm.ToUtc = Ast.AstToUtc(first.AddMonths(1));
                        vm.RangeLabel = first.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
                        break;
                }
            }

            int totalCount;
            decimal totalAmount;
            vm.Rows = GolaPayDb.GetPaidOrders(vm.FromUtc, vm.ToUtc, vm.Q, vm.Page,
                GolaStripe.Models.ReceivedPageVm.PageSize, out totalCount, out totalAmount);
            vm.TotalCount = totalCount;
            vm.TotalAmount = totalAmount;

            // Página fuera de rango → última página
            if (vm.Page > vm.PageCount && vm.TotalCount > 0)
            {
                vm.Page = vm.PageCount;
                vm.Rows = GolaPayDb.GetPaidOrders(vm.FromUtc, vm.ToUtc, vm.Q, vm.Page,
                    GolaStripe.Models.ReceivedPageVm.PageSize, out totalCount, out totalAmount);
            }

            return View(vm);
        }

        private static bool TryParseDay(string s, out DateTime day)
        {
            return DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out day);
        }

        // GET /Payments/Success?session_id=cs_test_...
        [PublicAccess]
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

        // GET /Payments/Order/{id}  (admin, protegido por DeviceGateFilter; solo inglés)
        [HttpGet]
        public ActionResult Order(string id)
        {
            ViewBag.Title = "Order";
            ViewBag.NoIndex = true;
            ViewBag.HideLangSwitch = true;
            ViewBag.Flash = TempData["OrderFlash"] as string;
            ViewBag.FlashError = TempData["OrderFlashError"] as string;

            long orderId;
            GolaStripe.Models.OrderDetailVm vm = null;
            if (long.TryParse((id ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out orderId) && orderId > 0)
            {
                try
                {
                    vm = GolaPayDb.GetOrderDetail(orderId);
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Order " + orderId + ": " + ex);
                    ViewBag.FlashError = "Could not load the order. Try again later.";
                }
            }

            if (vm == null)
            {
                Response.StatusCode = 404;
                Response.TrySkipIisCustomErrors = true;
                ViewBag.Title = "Order not found";
                return View((GolaStripe.Models.OrderDetailVm)null);
            }

            ViewBag.Title = "Order #" + vm.OrderId.ToString(CultureInfo.InvariantCulture);
            vm.ReceiptUrl = GetPublicBaseUrl() + "/Payments/Receipt?id=" + vm.PublicOrderId.ToString()
                + "&lang=" + Lang.NormalizeOrDefault(vm.Language);

            var dash = "https://dashboard.stripe.com" + (vm.Livemode ? "" : "/test");
            if (!string.IsNullOrEmpty(vm.StripePaymentIntentId))
            {
                vm.StripeDashboardUrl = dash + "/payments/" + Uri.EscapeDataString(vm.StripePaymentIntentId);
                vm.StripeDashboardLabel = "Open payment in Stripe";
            }
            else if (!string.IsNullOrEmpty(vm.StripeSessionId))
            {
                vm.StripeDashboardUrl = dash + "/checkout/sessions/" + Uri.EscapeDataString(vm.StripeSessionId);
                vm.StripeDashboardLabel = "Open checkout session in Stripe";
            }
            return View(vm);
        }

        // POST /Payments/ResendReceipt  (admin) — reenvía el mismo recibo en el idioma de la orden.
        // No toca ReceiptEmailSentAt (esa columna solo evita que el webhook envíe dos veces).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ResendReceipt(long? id)
        {
            if (!id.HasValue || id.Value <= 0)
                return RedirectToAction("Received");

            var orderId = id.Value;
            try
            {
                GolaStripe.Models.ReceiptVm receipt;
                if (!GolaPayDb.TryGetReceiptByOrderId(orderId, out receipt))
                {
                    TempData["OrderFlashError"] = "Order not found.";
                }
                else if (!receipt.IsPaid)
                {
                    TempData["OrderFlashError"] = "Only paid orders can have their receipt resent.";
                }
                else if (string.IsNullOrWhiteSpace(receipt.CustomerEmail))
                {
                    TempData["OrderFlashError"] = "This order has no customer email.";
                }
                else
                {
                    string error;
                    if (ReceiptMailer.TrySend(receipt, GetPublicBaseUrl(), out error))
                    {
                        Trace.TraceInformation("ResendReceipt: orden " + orderId + " reenviada a " + receipt.CustomerEmail
                            + " (dispositivo " + (DeviceAuth.Current != null ? DeviceAuth.Current.DeviceId.ToString(CultureInfo.InvariantCulture) : "?") + ")");
                        TempData["OrderFlash"] = "Receipt email sent again to " + receipt.CustomerEmail + ".";
                    }
                    else
                    {
                        Trace.TraceError("ResendReceipt: orden " + orderId + ": " + error);
                        TempData["OrderFlashError"] = "Could not send the receipt email: " + error;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("ResendReceipt: orden " + orderId + ": " + ex);
                TempData["OrderFlashError"] = "Could not send the receipt email. Try again later.";
            }

            return RedirectToAction("Order", new { id = orderId });
        }

        // GET /Payments/Receipt?id={PublicOrderId}  (link del email)
        [HttpGet]
        [PublicAccess]
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
        [PublicAccess]
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
        [PublicAccess]
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
                        amountDollars,
                        session.Livemode);

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

                    GolaPayDb.TryMarkOrderExpired(orderId.Value, session.Livemode);
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