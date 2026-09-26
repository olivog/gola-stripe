using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Web;
using GolaStripe.Models;

namespace GolaStripe.Helpers
{
    /// <summary>
    /// Email de recibo al cliente. Nunca lanza excepciones: devuelve bool + error.
    /// Config (appSettings): ReceiptFrom, ReceiptFromName, ReceiptReplyTo, ReceiptBcc, PublicBaseUrl, MailPickupDir.
    /// SMTP: system.net/mailSettings en Web.config (relay de GoDaddy, sin credenciales).
    /// </summary>
    public static class ReceiptMailer
    {
        // Montos: siempre USD en-US (Money.Format). Textos/fechas: idioma de la orden (Orders.Language),
        // porque el email sale del webhook y no hay navegador/cookie.
        private static string T(string key, CultureInfo c)
        {
            return Lang.Get(key, c);
        }

        /// <summary>
        /// Envía el recibo UNA sola vez por orden: claim atómico en DB (ReceiptEmailSentAt) → enviar →
        /// si falla, suelta el claim para que un reintento (webhook o página Success) lo vuelva a intentar.
        /// Devuelve true solo si este llamado envió el email.
        /// </summary>
        public static bool TrySendReceiptOnce(long orderId, string baseUrl, out string error)
        {
            error = null;
            try
            {
                ReceiptVm receipt;
                if (!GolaPayDb.TryGetReceiptByOrderId(orderId, out receipt))
                    return false;

                if (!receipt.IsPaid
                    || string.IsNullOrWhiteSpace(receipt.CustomerEmail)
                    || receipt.ReceiptEmailSentAtUtc.HasValue)
                    return false;

                if (!GolaPayDb.TryMarkReceiptEmailSent(orderId))
                    return false; // ya enviado (o lo está enviando otro request)

                if (TrySend(receipt, baseUrl, out error))
                    return true;

                try { GolaPayDb.ClearReceiptEmailSent(orderId); }
                catch (Exception ex2) { error += " | No se pudo soltar el claim: " + ex2.Message; }

                Trace.TraceError("ReceiptMailer: fallo enviando recibo de orden " + orderId + ": " + error);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Trace.TraceError("ReceiptMailer: error con orden " + orderId + ": " + ex);
                return false;
            }
        }

        /// <summary>Construye y envía el email. No toca la DB.</summary>
        public static bool TrySend(ReceiptVm receipt, string baseUrl, out string error)
        {
            error = null;
            try
            {
                if (receipt == null || string.IsNullOrWhiteSpace(receipt.CustomerEmail))
                {
                    error = "Recibo sin email de cliente.";
                    return false;
                }

                using (var msg = BuildMessage(receipt, baseUrl))
                using (var client = CreateClient())
                {
                    client.Send(msg);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException != null
                    ? ex.Message + " (" + ex.InnerException.Message + ")"
                    : ex.Message;
                return false;
            }
        }

        public static MailMessage BuildMessage(ReceiptVm r, string baseUrl)
        {
            var from = Setting("ReceiptFrom", "pagos@golapr.com");
            var fromName = Setting("ReceiptFromName", "Gola LLC");
            var replyTo = Setting("ReceiptReplyTo", null);
            var bcc = Setting("ReceiptBcc", null);

            baseUrl = NormalizeBaseUrl(baseUrl);
            var c = Lang.CultureFor(r.Language);

            var msg = new MailMessage();
            msg.From = new MailAddress(from, fromName, Encoding.UTF8);
            msg.To.Add(new MailAddress(r.CustomerEmail.Trim(), r.CustomerName ?? "", Encoding.UTF8));
            if (!string.IsNullOrEmpty(replyTo))
                msg.ReplyToList.Add(new MailAddress(replyTo));
            if (!string.IsNullOrEmpty(bcc))
                msg.Bcc.Add(new MailAddress(bcc));

            msg.Subject = string.Format(c, T("Email_Subject", c), fromName,
                r.OrderId.ToString(CultureInfo.InvariantCulture));
            msg.SubjectEncoding = Encoding.UTF8;
            msg.HeadersEncoding = Encoding.UTF8;
            msg.BodyEncoding = Encoding.UTF8;

            var text = AlternateView.CreateAlternateViewFromString(
                BuildText(r, baseUrl, c), Encoding.UTF8, MediaTypeNames.Text.Plain);
            text.TransferEncoding = TransferEncoding.QuotedPrintable;

            var html = AlternateView.CreateAlternateViewFromString(
                BuildHtml(r, baseUrl, c), Encoding.UTF8, MediaTypeNames.Text.Html);
            html.TransferEncoding = TransferEncoding.QuotedPrintable;

            msg.AlternateViews.Add(text);
            msg.AlternateViews.Add(html);
            return msg;
        }

        private static SmtpClient CreateClient()
        {
            // new SmtpClient() lee host/puerto/ssl de system.net/mailSettings (Web.config)
            var client = new SmtpClient();
            client.Timeout = 20000;

            var pickup = Setting("MailPickupDir", null);
            // Solo usar carpeta pickup si está configurada Y existe en esta máquina.
            // Así, si Web.secrets.config local llega al servidor por error, el servidor sigue usando el relay.
            if (!string.IsNullOrEmpty(pickup) && Directory.Exists(pickup))
            {
                client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
                client.PickupDirectoryLocation = pickup;
            }
            return client;
        }

        public static string ReceiptUrl(ReceiptVm r, string baseUrl)
        {
            return NormalizeBaseUrl(baseUrl) + "/Payments/Receipt?id=" + r.PublicOrderId.ToString()
                + "&lang=" + Lang.NormalizeOrDefault(r.Language);
        }

        private static string BuildText(ReceiptVm r, string baseUrl, CultureInfo c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Gola LLC - " + T("Receipt_Title", c));
            sb.AppendLine(string.Format(c, T("Receipt_OrderNumber", c), r.OrderId.ToString(CultureInfo.InvariantCulture)));
            var paid = PaidAtAst(r, c);
            if (paid != null) sb.AppendLine(T("Label_Date", c) + ": " + paid);
            if (!string.IsNullOrEmpty(r.CustomerName)) sb.AppendLine(T("Label_Customer", c) + ": " + r.CustomerName);
            sb.AppendLine();
            foreach (var l in r.Lines)
            {
                sb.AppendLine(l.Name + "  x" + l.Quantity.ToString(CultureInfo.InvariantCulture) + "  " + Money.Format(l.UnitAmount)
                    + "  = " + Money.Format(l.LineTotal));
            }
            sb.AppendLine();
            sb.AppendLine(T("Label_Total", c) + " (" + r.CurrencyUpper + "): " + Money.Format(r.AmountTotal));
            if (!string.IsNullOrEmpty(r.CardLast4))
                sb.AppendLine(T("Label_Card", c) + ": " + CardBrandLabel(r.CardBrand, c) + " ****" + r.CardLast4);
            sb.AppendLine();
            sb.AppendLine(T("Email_ViewReceipt", c) + ": " + ReceiptUrl(r, baseUrl));
            sb.AppendLine();
            sb.AppendLine(T("Email_ThankYou", c));
            sb.AppendLine("Gola LLC · golapr.com");
            return sb.ToString();
        }

        private static string BuildHtml(ReceiptVm r, string baseUrl, CultureInfo c)
        {
            Func<string, string> H = HttpUtility.HtmlEncode;
            var logoUrl = baseUrl + "/Content/images/gola-logo.png";
            var receiptUrl = ReceiptUrl(r, baseUrl);
            var paid = PaidAtAst(r, c);

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"" + Lang.NormalizeOrDefault(r.Language) + "\"><head><meta charset=\"utf-8\" />");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
            sb.Append("<title>" + H(T("Receipt_Title", c)) + "</title></head>");
            sb.Append("<body style=\"margin:0;padding:0;background:#f1f5f9;font-family:Arial,Helvetica,sans-serif;color:#0f172a;\">");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:#f1f5f9;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"max-width:600px;background:#ffffff;border-radius:8px;\">");

            // Logo + título
            sb.Append("<tr><td align=\"center\" style=\"padding:28px 24px 8px;\">");
            sb.Append("<img src=\"" + H(logoUrl) + "\" alt=\"Gola\" height=\"56\" style=\"display:block;height:56px;width:auto;border:0;\" />");
            sb.Append("</td></tr>");
            sb.Append("<tr><td align=\"center\" style=\"padding:8px 24px 0;font-size:22px;font-weight:bold;color:#0f172a;\">" + H(T("Receipt_Title", c)) + "</td></tr>");
            sb.Append("<tr><td align=\"center\" style=\"padding:4px 24px 20px;font-size:14px;color:#64748b;\">");
            sb.Append("<span style=\"display:inline-block;padding:2px 10px;border-radius:999px;background:#dcfce7;color:#166534;font-weight:bold;font-size:12px;\">" + H(T("Status_Paid", c)) + "</span>");
            sb.Append(" &middot; " + H(string.Format(c, T("Receipt_OrderNumber", c), r.OrderId.ToString(CultureInfo.InvariantCulture))));
            sb.Append("</td></tr>");

            // Datos
            sb.Append("<tr><td style=\"padding:0 24px 16px;font-size:14px;color:#334155;line-height:22px;\">");
            var greeting = string.IsNullOrEmpty(r.CustomerName)
                ? T("Email_Greeting", c)
                : string.Format(c, T("Email_GreetingName", c), r.CustomerName);
            sb.Append("<p style=\"margin:0 0 12px;\">" + H(greeting) + "</p>");
            if (!string.IsNullOrEmpty(r.CustomerName))
                sb.Append("<strong style=\"color:#0f172a;\">" + H(T("Label_Customer", c)) + ":</strong> " + H(r.CustomerName) + "<br />");
            if (paid != null)
                sb.Append("<strong style=\"color:#0f172a;\">" + H(T("Label_Date", c)) + ":</strong> " + H(paid) + "<br />");
            if (!string.IsNullOrEmpty(r.CardLast4))
                sb.Append("<strong style=\"color:#0f172a;\">" + H(T("Label_Card", c)) + ":</strong> " + H(CardBrandLabel(r.CardBrand, c)) + " ****" + H(r.CardLast4) + "<br />");
            sb.Append("</td></tr>");

            // Líneas
            sb.Append("<tr><td style=\"padding:0 24px;\">");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"font-size:14px;border-collapse:collapse;\">");
            sb.Append("<tr>");
            // Columnas numéricas: padding-left + nowrap para que no se peguen en móvil (Gmail iOS mostraba "1$4,000.00")
            sb.Append(Th(H(T("Col_Description", c)), "left"));
            sb.Append(Th(H(T("Col_Qty", c)), "right", true, QtyColWidth));
            sb.Append(Th(H(T("Col_Price", c)), "right", true));
            sb.Append(Th(H(T("Col_Amount", c)), "right", true));
            sb.Append("</tr>");
            if (r.Lines != null && r.Lines.Count > 0)
            {
                foreach (var l in r.Lines)
                {
                    sb.Append("<tr>");
                    sb.Append(Td(H(l.Name), "left"));
                    sb.Append(Td(H(l.Quantity.ToString(CultureInfo.InvariantCulture)), "right", true, QtyColWidth));
                    sb.Append(Td(H(Money.Format(l.UnitAmount)), "right", true));
                    sb.Append(Td(H(Money.Format(l.LineTotal)), "right", true));
                    sb.Append("</tr>");
                }
            }
            else
            {
                sb.Append("<tr><td colspan=\"4\" style=\"padding:10px 0;border-bottom:1px solid #f1f5f9;\">" + H(T("Receipt_NoItems", c)) + "</td></tr>");
            }
            sb.Append("<tr>");
            sb.Append("<td colspan=\"3\" style=\"padding:12px 0 0;border-top:2px solid #0f172a;font-size:16px;font-weight:bold;\">" + H(T("Label_Total", c)) + " (" + H(r.CurrencyUpper) + ")</td>");
            sb.Append("<td align=\"right\" style=\"padding:12px 0 0 12px;border-top:2px solid #0f172a;font-size:16px;font-weight:bold;white-space:nowrap;\">" + H(Money.Format(r.AmountTotal)) + "</td>");
            sb.Append("</tr>");
            sb.Append("</table>");
            sb.Append("</td></tr>");

            // Botón
            sb.Append("<tr><td align=\"center\" style=\"padding:28px 24px 8px;\">");
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr>");
            sb.Append("<td align=\"center\" bgcolor=\"#16a34a\" style=\"border-radius:6px;\">");
            sb.Append("<a href=\"" + H(receiptUrl) + "\" target=\"_blank\" style=\"display:inline-block;padding:12px 24px;font-size:15px;font-weight:bold;color:#ffffff;text-decoration:none;border-radius:6px;background:#16a34a;\">" + H(T("Email_ViewReceipt", c)) + "</a>");
            sb.Append("</td></tr></table>");
            sb.Append("</td></tr>");
            sb.Append("<tr><td align=\"center\" style=\"padding:4px 24px 24px;font-size:12px;color:#94a3b8;\">" + H(T("Label_Ref", c)) + ": " + H(r.PublicOrderId.ToString()) + "</td></tr>");

            // Footer
            sb.Append("<tr><td align=\"center\" style=\"padding:16px 24px;border-top:1px solid #e2e8f0;font-size:12px;color:#64748b;\">");
            sb.Append("Gola LLC &middot; <a href=\"https://golapr.com\" style=\"color:#64748b;\">golapr.com</a>");
            sb.Append("</td></tr>");

            sb.Append("</table>");
            sb.Append("</td></tr></table>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private const int QtyColWidth = 44; // px, columna QTY

        // numeric = columnas QTY / PRICE / AMOUNT: padding-left:12px + white-space:nowrap (separación visible en móvil)
        private static string Th(string text, string align, bool numeric = false, int widthPx = 0)
        {
            return "<th align=\"" + align + "\"" + WidthAttr(widthPx) + " style=\"padding:8px 0" + (numeric ? " 8px 12px" : "") + ";"
                + (numeric ? "white-space:nowrap;" : "") + (widthPx > 0 ? "width:" + widthPx + "px;" : "")
                + "border-bottom:2px solid #e2e8f0;color:#64748b;font-size:11px;text-transform:uppercase;text-align:" + align + ";\">" + text + "</th>";
        }

        private static string Td(string encodedHtml, string align, bool numeric = false, int widthPx = 0)
        {
            return "<td align=\"" + align + "\"" + WidthAttr(widthPx) + " style=\"padding:10px 0" + (numeric ? " 10px 12px" : "") + ";"
                + (numeric ? "white-space:nowrap;" : "") + (widthPx > 0 ? "width:" + widthPx + "px;" : "")
                + "border-bottom:1px solid #f1f5f9;vertical-align:top;\">" + encodedHtml + "</td>";
        }

        private static string WidthAttr(int widthPx)
        {
            return widthPx > 0 ? " width=\"" + widthPx + "\"" : "";
        }

        /// <summary>Fecha de pago en AST (UTC-4, Puerto Rico / La Paz, sin horario de verano), formato del idioma de la orden.</summary>
        private static string PaidAtAst(ReceiptVm r, CultureInfo c)
        {
            if (!r.PaidAtUtc.HasValue)
                return null;
            return Lang.FormatAst(r.PaidAtUtc.Value, c);
        }

        private static string CardBrandLabel(string brand, CultureInfo c)
        {
            if (string.IsNullOrEmpty(brand)) return T("Label_Card", c);
            switch (brand.ToLowerInvariant())
            {
                case "visa": return "Visa";
                case "mastercard": return "Mastercard";
                case "amex": return "American Express";
                case "discover": return "Discover";
                default: return char.ToUpperInvariant(brand[0]) + brand.Substring(1);
            }
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = Setting("PublicBaseUrl", "https://checkout.golapr.com");
            return baseUrl.Trim().TrimEnd('/');
        }

        private static string Setting(string key, string fallback)
        {
            var v = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }
    }
}
