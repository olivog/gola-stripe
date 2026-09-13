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
    }
}