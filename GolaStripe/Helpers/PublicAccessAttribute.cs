using System;

namespace GolaStripe.Helpers
{
    /// <summary>
    /// Marca una acción o controlador como público (clientes / Stripe). Todo lo que NO lo tenga
    /// requiere un dispositivo de confianza (cookie gola_device) — ver DeviceGateFilter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class PublicAccessAttribute : Attribute
    {
    }
}
