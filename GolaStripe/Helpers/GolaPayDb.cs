using System;
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
    (SourceAppId, Status, Livemode, Currency, AmountTotal, AmountSubtotal, MetadataJson)
OUTPUT INSERTED.OrderId, INSERTED.PublicOrderId
VALUES
    (@sourceAppId, 'Pending', 0, @currency, @amountTotal, @amountTotal, @metadata);", conn, tx))
                    {
                        cmd.Parameters.Add("@sourceAppId", SqlDbType.Int).Value = sourceAppId;
                        cmd.Parameters.Add("@currency", SqlDbType.Char, 3).Value = currency.ToLowerInvariant();
                        cmd.Parameters.Add("@amountTotal", SqlDbType.Decimal).Value = amountTotalDollars;
                        cmd.Parameters.Add("@metadata", SqlDbType.NVarChar, -1).Value =
                            "{\"origin\":\"gola-stripe\",\"product\":\"" + itemName.Replace("\"", "") + "\"}";

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
                        cmd.Parameters.Add("@name", SqlDbType.VarChar, 200).Value = itemName;
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

        public static void SetStripeSessionId(long orderId, string stripeSessionId)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET StripeSessionId = @sid, UpdatedAt = SYSUTCDATETIME()
WHERE OrderId = @orderId AND Status = 'Pending';", conn))
            {
                cmd.Parameters.Add("@sid", SqlDbType.VarChar, 128).Value = stripeSessionId;
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
    string stripeCustomerId,
    string paymentMethodType,
    string cardBrand,
    string cardLast4,
    string receiptUrl,
    decimal amountTotalDollars)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.Orders
SET Status = 'Paid',
    StripePaymentIntentId = @pi,
    CustomerEmail = COALESCE(@email, CustomerEmail),
    CustomerName = COALESCE(@name, CustomerName),
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
                cmd.Parameters.Add("@pi", SqlDbType.VarChar, 64).Value = (object)paymentIntentId ?? DBNull.Value;
                cmd.Parameters.Add("@email", SqlDbType.VarChar, 320).Value = (object)customerEmail ?? DBNull.Value;
                cmd.Parameters.Add("@name", SqlDbType.VarChar, 200).Value = (object)customerName ?? DBNull.Value;
                cmd.Parameters.Add("@cus", SqlDbType.VarChar, 64).Value = (object)stripeCustomerId ?? DBNull.Value;
                cmd.Parameters.Add("@pmType", SqlDbType.VarChar, 40).Value = (object)paymentMethodType ?? DBNull.Value;
                cmd.Parameters.Add("@brand", SqlDbType.VarChar, 40).Value = (object)cardBrand ?? DBNull.Value;
                cmd.Parameters.Add("@last4", SqlDbType.Char, 4).Value = (object)cardLast4 ?? DBNull.Value;
                cmd.Parameters.Add("@receipt", SqlDbType.VarChar, 500).Value = (object)receiptUrl ?? DBNull.Value;
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
                cmd.Parameters.Add("@eid", SqlDbType.VarChar, 64).Value = stripeEventId;
                cmd.Parameters.Add("@etype", SqlDbType.VarChar, 100).Value = eventType;
                cmd.Parameters.Add("@live", SqlDbType.Bit).Value = livemode;
                cmd.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = (object)payloadJson ?? DBNull.Value;
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return false;
                    var isNew = r.GetBoolean(0);
                    webhookEventId = r.GetInt64(1);
                    return isNew;
                }
            }
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
                cmd.Parameters.Add("@err", SqlDbType.VarChar, 1000).Value = (object)errorMessage ?? DBNull.Value;
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
                cmd.Parameters.Add("@sid", SqlDbType.VarChar, 128).Value = sessionId;
                conn.Open();
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? (long?)null : Convert.ToInt64(o);
            }
        }
    }
}