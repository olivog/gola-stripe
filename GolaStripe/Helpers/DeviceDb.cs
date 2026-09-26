using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using GolaStripe.Models;

namespace GolaStripe.Helpers
{
    /// <summary>ADO.NET para dbo.TrustedDevices y dbo.DeviceEnrollAttempts. Todo en UTC.</summary>
    public static class DeviceDb
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

        private const string DeviceColumns =
            "DeviceId, DeviceName, CreatedAt, LastSeenAt, LastIp, UserAgent, RevokedAt";

        private static TrustedDevice ReadDevice(SqlDataReader r)
        {
            return new TrustedDevice
            {
                DeviceId = r.GetInt32(0),
                DeviceName = r.IsDBNull(1) ? "" : r.GetString(1),
                CreatedAtUtc = r.GetDateTime(2),
                LastSeenAtUtc = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3),
                LastIp = r.IsDBNull(4) ? null : r.GetString(4),
                UserAgent = r.IsDBNull(5) ? null : r.GetString(5),
                RevokedAtUtc = r.IsDBNull(6) ? (DateTime?)null : r.GetDateTime(6)
            };
        }

        private static SqlParameter HashParam(byte[] tokenHash)
        {
            if (tokenHash == null || tokenHash.Length != 32)
                throw new ArgumentException("TokenHash debe ser SHA-256 de 32 bytes.", "tokenHash");
            return new SqlParameter("@hash", SqlDbType.VarBinary, 32) { Value = tokenHash };
        }

        /// <summary>Fit + DBNull para vacío (columnas anulables LastIp / UserAgent).</summary>
        private static object FitOrNull(string s, int max, string field)
        {
            var v = DbText.Fit(s, max, field);
            return string.IsNullOrEmpty(v) ? (object)DBNull.Value : v;
        }

        /// <summary>IP para DeviceEnrollAttempts.Ip (NOT NULL): "unknown" si viene vacía.</summary>
        private static string AttemptIp(string ip)
        {
            var v = DbText.Fit(ip, DbText.IpMax, "DeviceEnrollAttempts.Ip");
            return string.IsNullOrEmpty(v) ? "unknown" : v;
        }

        /// <summary>Dispositivo activo (RevokedAt NULL) con ese hash, o null.</summary>
        public static TrustedDevice FindActiveByHash(byte[] tokenHash)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(
                "SELECT " + DeviceColumns + " FROM dbo.TrustedDevices WHERE TokenHash = @hash AND RevokedAt IS NULL;", conn))
            {
                cmd.Parameters.Add(HashParam(tokenHash));
                conn.Open();
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? ReadDevice(r) : null;
            }
        }

        public static int InsertDevice(string deviceName, byte[] tokenHash, string ip, string userAgent)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
INSERT INTO dbo.TrustedDevices (DeviceName, TokenHash, LastSeenAt, LastIp, UserAgent)
OUTPUT INSERTED.DeviceId
VALUES (@name, @hash, SYSUTCDATETIME(), @ip, @ua);", conn))
            {
                cmd.Parameters.Add("@name", SqlDbType.NVarChar, DbText.DeviceNameMax).Value =
                    DbText.Fit(deviceName, DbText.DeviceNameMax, "TrustedDevices.DeviceName");
                cmd.Parameters.Add(HashParam(tokenHash));
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, DbText.IpMax).Value =
                    FitOrNull(ip, DbText.IpMax, "TrustedDevices.LastIp");
                cmd.Parameters.Add("@ua", SqlDbType.NVarChar, DbText.UserAgentMax).Value =
                    FitOrNull(userAgent, DbText.UserAgentMax, "TrustedDevices.UserAgent");
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>Actualiza LastSeenAt/LastIp/UserAgent solo si pasaron 10+ minutos. true si escribió.</summary>
        public static bool TouchDevice(int deviceId, string ip, string userAgent)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.TrustedDevices
SET LastSeenAt = SYSUTCDATETIME(), LastIp = @ip, UserAgent = @ua
WHERE DeviceId = @id
  AND RevokedAt IS NULL
  AND (LastSeenAt IS NULL OR LastSeenAt < DATEADD(MINUTE, -10, SYSUTCDATETIME()));", conn))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = deviceId;
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, DbText.IpMax).Value =
                    FitOrNull(ip, DbText.IpMax, "TrustedDevices.LastIp");
                cmd.Parameters.Add("@ua", SqlDbType.NVarChar, DbText.UserAgentMax).Value =
                    FitOrNull(userAgent, DbText.UserAgentMax, "TrustedDevices.UserAgent");
                conn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>Todos los dispositivos: activos primero, luego revocados; más nuevos primero.</summary>
        public static List<TrustedDevice> ListDevices()
        {
            var list = new List<TrustedDevice>();
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(
                "SELECT " + DeviceColumns + @" FROM dbo.TrustedDevices
ORDER BY CASE WHEN RevokedAt IS NULL THEN 0 ELSE 1 END, CreatedAt DESC;", conn))
            {
                conn.Open();
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(ReadDevice(r));
            }
            return list;
        }

        /// <summary>Revoca (RevokedAt = ahora). Nunca borra. true si cambió.</summary>
        public static bool RevokeDevice(int deviceId)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
UPDATE dbo.TrustedDevices SET RevokedAt = SYSUTCDATETIME()
WHERE DeviceId = @id AND RevokedAt IS NULL;", conn))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = deviceId;
                conn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>Intentos fallidos de esa IP en los últimos 15 minutos.</summary>
        public static int CountRecentFailedAttempts(string ip)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.DeviceEnrollAttempts
WHERE Ip = @ip AND Succeeded = 0
  AND AttemptedAt >= DATEADD(MINUTE, -15, SYSUTCDATETIME());", conn))
            {
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, DbText.IpMax).Value = AttemptIp(ip);
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public static void LogEnrollAttempt(string ip, bool succeeded)
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
INSERT INTO dbo.DeviceEnrollAttempts (Ip, Succeeded) VALUES (@ip, @ok);", conn))
            {
                cmd.Parameters.Add("@ip", SqlDbType.VarChar, DbText.IpMax).Value = AttemptIp(ip);
                cmd.Parameters.Add("@ok", SqlDbType.Bit).Value = succeeded;
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Limpieza oportunista: borra intentos de más de 30 días.</summary>
        public static int PurgeOldEnrollAttempts()
        {
            using (var conn = new SqlConnection(Cs))
            using (var cmd = new SqlCommand(@"
DELETE FROM dbo.DeviceEnrollAttempts WHERE AttemptedAt < DATEADD(DAY, -30, SYSUTCDATETIME());", conn))
            {
                conn.Open();
                return cmd.ExecuteNonQuery();
            }
        }
    }
}
