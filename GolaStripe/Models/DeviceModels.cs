using System;

namespace GolaStripe.Models
{
    /// <summary>Fila de dbo.TrustedDevices (sin TokenHash). Fechas en UTC.</summary>
    public class TrustedDevice
    {
        public int DeviceId { get; set; }
        public string DeviceName { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? LastSeenAtUtc { get; set; }
        public string LastIp { get; set; }
        public string UserAgent { get; set; }
        public DateTime? RevokedAtUtc { get; set; }

        public bool IsActive
        {
            get { return !RevokedAtUtc.HasValue; }
        }
    }
}
