using System;
using System.Collections.Generic;

namespace GolaStripe.Models
{
    /// <summary>Una línea de dbo.OrderItems para /Payments/Order/{id}.</summary>
    public class OrderDetailLine
    {
        public string Name { get; set; }
        public int Quantity { get; set; }
        public decimal UnitAmount { get; set; }

        public decimal LineTotal
        {
            get { return UnitAmount * Quantity; }
        }
    }

    /// <summary>Detalle admin de una orden (dbo.Orders + dbo.OrderItems). Fechas en UTC (se muestran en AST).</summary>
    public class OrderDetailVm
    {
        public long OrderId { get; set; }
        public Guid PublicOrderId { get; set; }
        public string Status { get; set; }
        public bool Livemode { get; set; }
        public string Currency { get; set; }
        public decimal AmountTotal { get; set; }
        public string CustomerEmail { get; set; }
        public string CustomerName { get; set; }
        public string CustomerCountry { get; set; }
        public string StripeCustomerId { get; set; }
        public string StripeSessionId { get; set; }
        public string StripePaymentIntentId { get; set; }
        public string PaymentMethodType { get; set; }
        public string CardBrand { get; set; }
        public string CardLast4 { get; set; }
        public string FailureCode { get; set; }
        public string FailureMessage { get; set; }
        public string Language { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        /// <summary>dbo.Orders.CancelledAt: se usa tanto para Cancelled como para Expired (no hay columna ExpiredAt).</summary>
        public DateTime? CancelledAtUtc { get; set; }
        public DateTime? ReceiptEmailSentAtUtc { get; set; }
        public List<OrderDetailLine> Lines { get; set; }

        // Calculados por el controller
        public string ReceiptUrl { get; set; }
        public string StripeDashboardUrl { get; set; }
        public string StripeDashboardLabel { get; set; }

        public OrderDetailVm()
        {
            Lines = new List<OrderDetailLine>();
        }

        public bool IsPaid
        {
            get { return string.Equals(Status, "Paid", StringComparison.OrdinalIgnoreCase); }
        }

        public bool CanResendReceipt
        {
            get { return IsPaid && !string.IsNullOrWhiteSpace(CustomerEmail); }
        }
    }
}
