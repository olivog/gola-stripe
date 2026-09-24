using System;
using System.Collections.Generic;

namespace GolaStripe.Models
{
    public class ReceiptLine
    {
        public string Name { get; set; }
        public int Quantity { get; set; }
        public decimal UnitAmount { get; set; } // dólares
        public string Currency { get; set; }

        public decimal LineTotal
        {
            get { return Math.Round(UnitAmount * Quantity, 2, MidpointRounding.AwayFromZero); }
        }
    }

    public class ReceiptVm
    {
        public long OrderId { get; set; }
        public Guid PublicOrderId { get; set; }
        public string Status { get; set; }
        public string CustomerName { get; set; }
        public string CustomerEmail { get; set; }
        public string CustomerCountry { get; set; }
        public string Currency { get; set; }
        public decimal AmountTotal { get; set; } // dólares
        public string CardBrand { get; set; }
        public string CardLast4 { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        public string StripeSessionId { get; set; }
        public List<ReceiptLine> Lines { get; set; }

        public ReceiptVm()
        {
            Lines = new List<ReceiptLine>();
            Currency = "usd";
        }

        public string CurrencyUpper
        {
            get { return string.IsNullOrEmpty(Currency) ? "USD" : Currency.ToUpperInvariant(); }
        }
    }
}
