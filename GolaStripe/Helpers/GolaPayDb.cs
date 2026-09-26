using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;

namespace GolaStripe.Helpers
{
    public static class GolaPayDb
    {
        private static string Cs
        {
            get
            {
                var cs = ConfigurationManager.AppSettings["GolaPayConnectionString"];
                if (string.IsNullOrWhiteSpace(cs))
                    throw new InvalidOperationException("Falta GolaPayConnectionString en Web.secrets.config");
                return cs;
            }
        }

        public static int GetSourceAppId(string slug)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(
                "SELECT SourceAppId FROM dbo.SourceApps WHERE Slug = @slug AND IsActive = 1", conn))
            {
                cmd.Parameters.Add("@slug", SqlDbType.VarChar, 50).Value = slug;
                conn.Open();
                var o = cmd.ExecuteScalar();
                if (o == null || o == DBNull.Value)
                    throw new InvalidOperationException("SourceApp no encontrado: " + slug);
                return Convert.ToInt32(o);
            }
        }

        /// <summary>Crea Order Pending + 1 OrderItem. Devuelve OrderId y PublicOrderId.</summary>
        public static void CreatePendingOrder(
            int sourceAppId,
            decimal amountTotalDollars,   // ej. 10.00m o 10.50m
            string currency,
            string itemName,
            int quantity,
            string language,              // "en" / "es" → Orders.Language (nunca NULL)
            out long orderId,
            out Guid publicOrderId)
        {
            orderId = 0;
            publicOrderId = Guid.Empty;

            using (var conn = new SqlConnection(Cs))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    long newOrderId;
                    Guid newPublicId;

                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.Orders
    (SourceAppId, Status, Livemode, Currency, AmountTotal, AmountSubtotal, MetadataJson, Language)
OUTPUT INSERTED.OrderId, INSERTED.PublicOrderId
VALUES
    (@sourceAppId, 'Pending', 0, @currency, @amountTotal, @amountTotal, @metadata, @language);", conn, tx))
                    {
                        cmd.Parameters.Add("@sourceAppId", SqlDbType.Int).Value = sourceAppId;
                        cmd.Parameters.Add("@currency", SqlDbType.Char, 3).Value = currency.ToLowerInvariant();
                        cmd.Parameters.Add("@amountTotal", SqlDbType.Decimal).Value = amountTotalDollars;
                        cmd.Parameters.Add("@metadata", SqlDbType.NVarChar, -1).Value =
                            "{\"origin\":\"gola-stripe\",\"product\":\"" + itemName.Replace("\"", "") + "\"}";
                        cmd.Parameters.Add("@language", SqlDbType.VarChar, 10).Value =
                            GolaStripe.Helpers.Lang.NormalizeOrDefault(language);

                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read())
                                throw new InvalidOperationException("No se pudo crear Orders");
                            newOrderId = r.GetInt64(0);
                            newPublicId = r.GetGuid(1);
                        }
                    }

                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.OrderItems (OrderId, Name, Quantity, UnitAmount, Currency)
VALUES (@orderId, @name, @qty, @unitAmount, @currency);", conn, tx))
                    {
                        cmd.Parameters.Add("@orderId", SqlDbType.BigInt).Value = newOrderId;
                        cmd.Parameters.Add("@name", SqlDbType.NVarChar, DbText.ItemNameMax).Value =
                            DbText.Fit(itemName, DbText.ItemNameMax, "OrderItems.Name");
                        cmd.Parameters.Add("@qty", SqlDbType.Int).Value = quantity;
                        cmd.Parameters.Add("@unitAmount", SqlDbType.Decimal).Value =
                            Math.Round(amountTotalDollars / quantity, 2, MidpointRounding.AwayFromZero);
                        cmd.Parameters.Add("@currency", SqlDbType.Char, 3).Value = currency.ToLowerInvariant();
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    orderId = newOrderId;
                    publicOrderId = newPublicId;
                }
            }
        }

        /// <summary>Guarda el id de la sesión recién creada y su modo (Session.Livemode: cs_live_ → 1, cs_test_ → 0).</summary>
        public static void SetStripeSessionId(long orderId, string stripeSessionId, bool livemode)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET StripeSessionId = @sid, Livemode = @live, UpdatedAt = SYSUTCDATETIME()
WHERE OrderId = @orderId AND Status = 'Pending';", conn))
            {
                cmd.Parameters.Add("@live", SqlDbType.Bit).Value = livemode;
                cmd.Parameters.Add("@sid", SqlDbType.VarChar, DbText.StripeIdMax).Value =
                    DbText.Fit(stripeSessionId, DbText.StripeIdMax, "Orders.StripeSessionId");
                cmd.Parameters.Add("@orderId", SqlDbType.BigInt).Value = orderId;
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }
        public static void MarkOrderPaidFromCheckoutSession(
    long orderId,
    string paymentIntentId,
    string customerEmail,
    string customerName,
    string customerCountry,
    string stripeCustomerId,
    string paymentMethodType,
    string cardBrand,
    string cardLast4,
    string receiptUrl,
    decimal amountTotalDollars,
    bool livemode)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET Status = 'Paid',
    Livemode = @live,
    StripePaymentIntentId = @pi,
    CustomerEmail = COALESCE(@email, CustomerEmail),
    CustomerName = COALESCE(@name, CustomerName),
    CustomerCountry = COALESCE(@country, CustomerCountry),
    StripeCustomerId = COALESCE(@cus, StripeCustomerId),
    PaymentMethodType = @pmType,
    CardBrand = @brand,
    CardLast4 = @last4,
    ReceiptUrl = @receipt,
    AmountTotal = @amount,
    PaidAt = SYSUTCDATETIME(),
    UpdatedAt = SYSUTCDATETIME()
WHERE OrderId = @orderId
  AND Status IN ('Pending','Paid');", conn))
            {
                cmd.Parameters.Add("@orderId", SqlDbType.BigInt).Value = orderId;
                cmd.Parameters.Add("@live", SqlDbType.Bit).Value = livemode;
                cmd.Parameters.Add("@pi", SqlDbType.VarChar, DbText.StripeIdMax).Value =
                    DbText.FitOrNull(paymentIntentId, DbText.StripeIdMax, "Orders.StripePaymentIntentId");
                cmd.Parameters.Add("@email", SqlDbType.VarChar, DbText.CustomerEmailMax).Value =
                    DbText.FitOrNull(customerEmail, DbText.CustomerEmailMax, "Orders.CustomerEmail");
                cmd.Parameters.Add("@name", SqlDbType.NVarChar, DbText.CustomerNameMax).Value =
                    DbText.FitOrNull(customerName, DbText.CustomerNameMax, "Orders.CustomerName");
                cmd.Parameters.Add("@country", SqlDbType.Char, DbText.CountryMax).Value =
                    DbText.FitOrNull(customerCountry, DbText.CountryMax, "Orders.CustomerCountry");
                cmd.Parameters.Add("@cus", SqlDbType.VarChar, DbText.StripeIdMax).Value =
                    DbText.FitOrNull(stripeCustomerId, DbText.StripeIdMax, "Orders.StripeCustomerId");
                cmd.Parameters.Add("@pmType", SqlDbType.VarChar, DbText.PaymentMethodTypeMax).Value =
                    DbText.FitOrNull(paymentMethodType, DbText.PaymentMethodTypeMax, "Orders.PaymentMethodType");
                cmd.Parameters.Add("@brand", SqlDbType.VarChar, DbText.CardBrandMax).Value =
                    DbText.FitOrNull(cardBrand, DbText.CardBrandMax, "Orders.CardBrand");
                cmd.Parameters.Add("@last4", SqlDbType.Char, DbText.CardLast4Max).Value =
                    DbText.FitOrNull(cardLast4, DbText.CardLast4Max, "Orders.CardLast4");
                cmd.Parameters.Add("@receipt", SqlDbType.VarChar, DbText.ReceiptUrlMax).Value =
                    DbText.FitOrNull(receiptUrl, DbText.ReceiptUrlMax, "Orders.ReceiptUrl");
                var pAmt = cmd.Parameters.Add("@amount", SqlDbType.Decimal);
                pAmt.Precision = 18;
                pAmt.Scale = 2;
                pAmt.Value = amountTotalDollars;
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        /// <returns>true si es evento nuevo; false si ya existía</returns>
        public static bool TryBeginWebhookEvent(
            string stripeEventId,
            string eventType,
            bool livemode,
            string payloadJson,
            out long webhookEventId)
        {
            webhookEventId = 0;
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
IF EXISTS (SELECT 1 FROM dbo.WebhookEvents WHERE StripeEventId = @eid)
BEGIN
  SELECT CAST(0 AS BIT), WebhookEventId FROM dbo.WebhookEvents WHERE StripeEventId = @eid;
END
ELSE
BEGIN
  INSERT INTO dbo.WebhookEvents (StripeEventId, EventType, Livemode, PayloadJson, ProcessingStatus)
  OUTPUT CAST(1 AS BIT), INSERTED.WebhookEventId
  VALUES (@eid, @etype, @live, @payload, 'Received');
END", conn))
            {
                cmd.Parameters.Add("@eid", SqlDbType.VarChar, DbText.StripeIdMax).Value =
                    DbText.Fit(stripeEventId, DbText.StripeIdMax, "WebhookEvents.StripeEventId");
                cmd.Parameters.Add("@etype", SqlDbType.VarChar, DbText.EventTypeMax).Value =
                    DbText.Fit(eventType, DbText.EventTypeMax, "WebhookEvents.EventType");
                cmd.Parameters.Add("@live", SqlDbType.Bit).Value = livemode;
                cmd.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = (object)payloadJson ?? DBNull.Value;
                conn.Open();
                try
                {
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return false;
                        var isNew = r.GetBoolean(0);
                        webhookEventId = r.GetInt64(1);
                        return isNew;
                    }
                }
                catch (SqlException ex) when (IsDuplicateKey(ex))
                {
                    // Carrera: Stripe entregó el mismo evento dos veces a la vez y el otro request insertó primero.
                    using (var cmd2 = new SqlCommand(
                        "SELECT WebhookEventId FROM dbo.WebhookEvents WHERE StripeEventId = @eid;", conn))
                    {
                        cmd2.Parameters.Add("@eid", SqlDbType.VarChar, DbText.StripeIdMax).Value =
                            DbText.Fit(stripeEventId, DbText.StripeIdMax);
                        var o = cmd2.ExecuteScalar();
                        webhookEventId = o == null || o == DBNull.Value ? 0 : Convert.ToInt64(o);
                    }
                    return false;
                }
            }
        }

        /// <summary>Violación de UNIQUE: 2627 (constraint/índice único) o 2601 (índice único, p. ej. filtrado).</summary>
        public static bool IsDuplicateKey(SqlException ex)
        {
            return ex != null && (ex.Number == 2627 || ex.Number == 2601);
        }

        public static void CompleteWebhookEvent(
            long webhookEventId,
            long? relatedOrderId,
            string status,
            string errorMessage)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.WebhookEvents
SET ProcessingStatus = @status,
    RelatedOrderId = @orderId,
    ErrorMessage = @err,
    ProcessedAt = SYSUTCDATETIME()
WHERE WebhookEventId = @id;", conn))
            {
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = webhookEventId;
                cmd.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = status;
                cmd.Parameters.Add("@orderId", SqlDbType.BigInt).Value = (object)relatedOrderId ?? DBNull.Value;
                cmd.Parameters.Add("@err", SqlDbType.VarChar, DbText.ErrorMessageMax).Value =
                    DbText.FitOrNull(errorMessage, DbText.ErrorMessageMax, "WebhookEvents.ErrorMessage");
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public static long? FindOrderIdByPublicOrderId(Guid publicOrderId)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(
                "SELECT OrderId FROM dbo.Orders WHERE PublicOrderId = @pid", conn))
            {
                cmd.Parameters.Add("@pid", SqlDbType.UniqueIdentifier).Value = publicOrderId;
                conn.Open();
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? (long?)null : Convert.ToInt64(o);
            }
        }

        public static long? FindOrderIdBySessionId(string sessionId)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(
                "SELECT OrderId FROM dbo.Orders WHERE StripeSessionId = @sid", conn))
            {
                cmd.Parameters.Add("@sid", SqlDbType.VarChar, DbText.StripeIdMax).Value = sessionId;
                conn.Open();
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? (long?)null : Convert.ToInt64(o);
            }
        }
    
        public static bool TryGetReceiptBySessionId(string sessionId, out GolaStripe.Models.ReceiptVm receipt)
        {
            receipt = null;
            if (string.IsNullOrWhiteSpace(sessionId))
                return false;

            var p = new SqlParameter("@sid", SqlDbType.VarChar, DbText.StripeIdMax) { Value = sessionId };
            return TryGetReceipt("StripeSessionId = @sid", p, out receipt);
        }

        /// <summary>Recibo por PublicOrderId (link público /Payments/Receipt?id=...).</summary>
        public static bool TryGetReceiptByPublicId(Guid publicOrderId, out GolaStripe.Models.ReceiptVm receipt)
        {
            receipt = null;
            if (publicOrderId == Guid.Empty)
                return false;

            var p = new SqlParameter("@pid", SqlDbType.UniqueIdentifier) { Value = publicOrderId };
            return TryGetReceipt("PublicOrderId = @pid", p, out receipt);
        }

        /// <summary>Recibo por OrderId (webhook / envío de email).</summary>
        public static bool TryGetReceiptByOrderId(long orderId, out GolaStripe.Models.ReceiptVm receipt)
        {
            var p = new SqlParameter("@oid", SqlDbType.BigInt) { Value = orderId };
            return TryGetReceipt("OrderId = @oid", p, out receipt);
        }

        /// <summary>Carga Orders + OrderItems. whereSql es fijo (no viene del usuario); el valor va en el parámetro.</summary>
        private static bool TryGetReceipt(string whereSql, SqlParameter whereParam, out GolaStripe.Models.ReceiptVm receipt)
        {
            receipt = null;

            using (var conn = new SqlConnection(Cs))
            {
                conn.Open();

                GolaStripe.Models.ReceiptVm vm = null;
                using (var cmd = new SqlCommand(@"
SELECT OrderId, PublicOrderId, Status, CustomerEmail, CustomerName, CustomerCountry,
       Currency, AmountTotal, CardBrand, CardLast4, PaidAt, StripeSessionId, ReceiptEmailSentAt,
       Language
FROM dbo.Orders
WHERE " + whereSql + ";", conn))
                {
                    cmd.Parameters.Add(whereParam);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read())
                            return false;

                        vm = new GolaStripe.Models.ReceiptVm
                        {
                            OrderId = r.GetInt64(0),
                            PublicOrderId = r.GetGuid(1),
                            Status = r.IsDBNull(2) ? null : r.GetString(2),
                            CustomerEmail = r.IsDBNull(3) ? null : r.GetString(3),
                            CustomerName = r.IsDBNull(4) ? null : r.GetString(4),
                            CustomerCountry = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                            Currency = r.IsDBNull(6) ? "usd" : r.GetString(6).Trim(),
                            AmountTotal = r.IsDBNull(7) ? 0m : r.GetDecimal(7),
                            CardBrand = r.IsDBNull(8) ? null : r.GetString(8),
                            CardLast4 = r.IsDBNull(9) ? null : r.GetString(9),
                            PaidAtUtc = r.IsDBNull(10) ? (DateTime?)null : r.GetDateTime(10),
                            StripeSessionId = r.IsDBNull(11) ? null : r.GetString(11),
                            ReceiptEmailSentAtUtc = r.IsDBNull(12) ? (DateTime?)null : r.GetDateTime(12),
                            Language = r.IsDBNull(13) ? "en" : r.GetString(13).Trim().ToLowerInvariant()
                        };
                    }
                }

                using (var cmd = new SqlCommand(@"
SELECT Name, Quantity, UnitAmount, Currency
FROM dbo.OrderItems
WHERE OrderId = @oid
ORDER BY OrderItemId;", conn))
                {
                    cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = vm.OrderId;
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            vm.Lines.Add(new GolaStripe.Models.ReceiptLine
                            {
                                Name = r.IsDBNull(0) ? "" : r.GetString(0),
                                Quantity = r.GetInt32(1),
                                UnitAmount = r.IsDBNull(2) ? 0m : r.GetDecimal(2),
                                Currency = r.IsDBNull(3) ? vm.Currency : r.GetString(3).Trim()
                            });
                        }
                    }
                }

                receipt = vm;
                return true;
            }
        }

        /// <summary>
        /// "Reclama" el envío del recibo por email de forma atómica (solo órdenes Paid con email y sin enviar).
        /// Devuelve true si este proceso ganó el claim y debe enviar; false si ya se envió / otro lo está enviando.
        /// </summary>
        public static bool TryMarkReceiptEmailSent(long orderId)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET ReceiptEmailSentAt = SYSUTCDATETIME()
WHERE OrderId = @oid
  AND Status = 'Paid'
  AND ReceiptEmailSentAt IS NULL
  AND CustomerEmail IS NOT NULL
  AND CustomerEmail <> '';", conn))
            {
                cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = orderId;
                conn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>Si el envío falló, suelta el claim para que un reintento pueda enviar.</summary>
        public static void ClearReceiptEmailSent(long orderId)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET ReceiptEmailSentAt = NULL
WHERE OrderId = @oid;", conn))
            {
                cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = orderId;
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Pending → Cancelled (clic en Cancel de Checkout). Devuelve true si cambió.</summary>
        public static bool TryMarkOrderCancelledBySessionId(string sessionId, out long? orderId, out string status)
        {
            orderId = null;
            status = null;
            if (string.IsNullOrWhiteSpace(sessionId))
                return false;

            using (var conn = new SqlConnection(Cs))
            {
                conn.Open();

                using (var cmd = new SqlCommand(@"
SELECT OrderId, Status FROM dbo.Orders WHERE StripeSessionId = @sid;", conn))
                {
                    cmd.Parameters.Add("@sid", SqlDbType.VarChar, DbText.StripeIdMax).Value = sessionId;
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read())
                            return false;
                        orderId = r.GetInt64(0);
                        status = r.IsDBNull(1) ? null : r.GetString(1);
                    }
                }

                if (!string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
                    return false;

                using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET Status = 'Cancelled',
    CancelledAt = SYSUTCDATETIME(),
    UpdatedAt = SYSUTCDATETIME()
WHERE OrderId = @oid
  AND Status = 'Pending';", conn))
                {
                    cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = orderId.Value;
                    var n = cmd.ExecuteNonQuery();
                    if (n > 0)
                    {
                        status = "Cancelled";
                        return true;
                    }
                }

                using (var cmd = new SqlCommand(
                    "SELECT Status FROM dbo.Orders WHERE OrderId = @oid", conn))
                {
                    cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = orderId.Value;
                    var o = cmd.ExecuteScalar();
                    status = o == null || o == DBNull.Value ? status : Convert.ToString(o);
                }
                return false;
            }
        }

        /// <summary>Pending → Expired (webhook checkout.session.expired). No pisa Cancelled/Paid.</summary>
        /// <summary>
        /// Pending → Expired. livemode: Session.Livemode si viene de un evento de Stripe;
        /// null (p. ej. CreateCheckout falló antes de tener sesión) deja Orders.Livemode como está.
        /// </summary>
        public static bool TryMarkOrderExpired(long orderId, bool? livemode = null)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET Status = 'Expired',
    Livemode = COALESCE(@live, Livemode),
    CancelledAt = COALESCE(CancelledAt, SYSUTCDATETIME()),
    UpdatedAt = SYSUTCDATETIME()
WHERE OrderId = @oid
  AND Status = 'Pending';", conn))
            {
                cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = orderId;
                cmd.Parameters.Add("@live", SqlDbType.Bit).Value = livemode.HasValue ? (object)livemode.Value : DBNull.Value;
                conn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// Órdenes Paid para /Payments/Received (solo lectura). Filtra por COALESCE(PaidAt, CreatedAt) en
        /// [fromUtc, toUtc) y por texto (nombre, email o nombre del ítem). Devuelve una página + totales del filtro.
        /// </summary>
        /// <summary>Detalle admin (/Payments/Order/{id}). null si no existe.</summary>
        public static GolaStripe.Models.OrderDetailVm GetOrderDetail(long orderId)
        {
            using (var conn = new SqlConnection(Cs))
            {
                conn.Open();
                GolaStripe.Models.OrderDetailVm vm;
                using (var cmd = new SqlCommand(@"
SELECT OrderId, PublicOrderId, Status, Livemode, Currency, AmountTotal,
       CustomerEmail, CustomerName, CustomerCountry,
       StripeCustomerId, StripeSessionId, StripePaymentIntentId,
       PaymentMethodType, CardBrand, CardLast4, FailureCode, FailureMessage, Language,
       CreatedAt, UpdatedAt, PaidAt, CancelledAt, ReceiptEmailSentAt
FROM dbo.Orders
WHERE OrderId = @oid;", conn))
                {
                    cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = orderId;
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read())
                            return null;

                        Func<int, string> str = i => r.IsDBNull(i) ? null : r.GetString(i).Trim();
                        Func<int, DateTime?> dt = i => r.IsDBNull(i) ? (DateTime?)null : r.GetDateTime(i);
                        vm = new GolaStripe.Models.OrderDetailVm
                        {
                            OrderId = r.GetInt64(0),
                            PublicOrderId = r.GetGuid(1),
                            Status = str(2),
                            Livemode = r.GetBoolean(3),
                            Currency = str(4) ?? "usd",
                            AmountTotal = r.GetDecimal(5),
                            CustomerEmail = str(6),
                            CustomerName = str(7),
                            CustomerCountry = str(8),
                            StripeCustomerId = str(9),
                            StripeSessionId = str(10),
                            StripePaymentIntentId = str(11),
                            PaymentMethodType = str(12),
                            CardBrand = str(13),
                            CardLast4 = str(14),
                            FailureCode = str(15),
                            FailureMessage = str(16),
                            Language = (str(17) ?? "en").ToLowerInvariant(),
                            CreatedAtUtc = r.GetDateTime(18),
                            UpdatedAtUtc = dt(19),
                            PaidAtUtc = dt(20),
                            CancelledAtUtc = dt(21),
                            ReceiptEmailSentAtUtc = dt(22)
                        };
                    }
                }

                using (var cmd = new SqlCommand(@"
SELECT Name, Quantity, UnitAmount
FROM dbo.OrderItems
WHERE OrderId = @oid
ORDER BY OrderItemId;", conn))
                {
                    cmd.Parameters.Add("@oid", SqlDbType.BigInt).Value = orderId;
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            vm.Lines.Add(new GolaStripe.Models.OrderDetailLine
                            {
                                Name = r.IsDBNull(0) ? "" : r.GetString(0),
                                Quantity = r.GetInt32(1),
                                UnitAmount = r.GetDecimal(2)
                            });
                        }
                    }
                }
                return vm;
            }
        }

        public static List<GolaStripe.Models.ReceivedPaymentRow> GetPaidOrders(
            DateTime? fromUtc, DateTime? toUtc, string search, int page, int pageSize,
            out int totalCount, out decimal totalAmount)
        {
            totalCount = 0;
            totalAmount = 0m;
            var rows = new List<GolaStripe.Models.ReceivedPaymentRow>();
            if (page < 1) page = 1;

            const string where = @"
WHERE o.Status = 'Paid'
  AND (@from IS NULL OR COALESCE(o.PaidAt, o.CreatedAt) >= @from)
  AND (@to   IS NULL OR COALESCE(o.PaidAt, o.CreatedAt) <  @to)
  AND (@q IS NULL
       OR o.CustomerName  LIKE @q ESCAPE '\'
       OR o.CustomerEmail LIKE @q ESCAPE '\'
       OR EXISTS (SELECT 1 FROM dbo.OrderItems i
                  WHERE i.OrderId = o.OrderId AND i.Name LIKE @q ESCAPE '\'))";

            string like = null;
            if (!string.IsNullOrWhiteSpace(search))
            {
                // Escapa los comodines de LIKE para que el texto se busque tal cual
                like = "%" + search.Trim()
                    .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
            }

            using (var conn = new SqlConnection(Cs))
            {
                conn.Open();

                using (var cmd = new SqlCommand(@"
SELECT COUNT(*), ISNULL(SUM(o.AmountTotal), 0)
FROM dbo.Orders o" + where + ";", conn))
                {
                    AddPaidOrderFilters(cmd, fromUtc, toUtc, like);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            totalCount = r.GetInt32(0);
                            totalAmount = r.GetDecimal(1);
                        }
                    }
                }

                using (var cmd = new SqlCommand(@"
SELECT o.OrderId, o.PublicOrderId, o.PaidAt, o.CreatedAt, o.CustomerName, o.CustomerEmail,
       o.AmountTotal, o.Currency, o.Language, o.Livemode,
       it.Name, ISNULL(cnt.ItemCount, 0)
FROM dbo.Orders o
OUTER APPLY (SELECT TOP 1 i.Name FROM dbo.OrderItems i
             WHERE i.OrderId = o.OrderId ORDER BY i.OrderItemId) it
OUTER APPLY (SELECT COUNT(*) AS ItemCount FROM dbo.OrderItems i2
             WHERE i2.OrderId = o.OrderId) cnt" + where + @"
ORDER BY COALESCE(o.PaidAt, o.CreatedAt) DESC, o.OrderId DESC
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;", conn))
                {
                    AddPaidOrderFilters(cmd, fromUtc, toUtc, like);
                    cmd.Parameters.Add("@skip", SqlDbType.Int).Value = (page - 1) * pageSize;
                    cmd.Parameters.Add("@take", SqlDbType.Int).Value = pageSize;
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            rows.Add(new GolaStripe.Models.ReceivedPaymentRow
                            {
                                OrderId = r.GetInt64(0),
                                PublicOrderId = r.GetGuid(1),
                                PaidAtUtc = r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2),
                                CreatedAtUtc = r.GetDateTime(3),
                                CustomerName = r.IsDBNull(4) ? null : r.GetString(4),
                                CustomerEmail = r.IsDBNull(5) ? null : r.GetString(5),
                                AmountTotal = r.GetDecimal(6),
                                Currency = r.IsDBNull(7) ? "usd" : r.GetString(7).Trim(),
                                Language = r.IsDBNull(8) ? "en" : r.GetString(8).Trim().ToLowerInvariant(),
                                Livemode = !r.IsDBNull(9) && r.GetBoolean(9),
                                Product = r.IsDBNull(10) ? null : r.GetString(10),
                                ItemCount = r.GetInt32(11)
                            });
                        }
                    }
                }
            }
            return rows;
        }

        private static void AddPaidOrderFilters(SqlCommand cmd, DateTime? fromUtc, DateTime? toUtc, string like)
        {
            cmd.Parameters.Add("@from", SqlDbType.DateTime2).Value = (object)fromUtc ?? DBNull.Value;
            cmd.Parameters.Add("@to", SqlDbType.DateTime2).Value = (object)toUtc ?? DBNull.Value;
            cmd.Parameters.Add("@q", SqlDbType.NVarChar, 420).Value = (object)like ?? DBNull.Value;
        }
    }
}