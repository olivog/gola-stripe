using System;
using System.Collections.Generic;

namespace GolaStripe.Models
{
    /// <summary>Una fila de /Payments/Received (orden Paid).</summary>
    public class ReceivedPaymentRow
    {
        public long OrderId { get; set; }
        public Guid PublicOrderId { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string CustomerName { get; set; }
        public string CustomerEmail { get; set; }
        public string Product { get; set; }
        public int ItemCount { get; set; }
        public decimal AmountTotal { get; set; }
        public string Currency { get; set; }
        public string Language { get; set; }
        public bool Livemode { get; set; }

        /// <summary>Fecha usada para ordenar/filtrar: PaidAt, o CreatedAt si no hay PaidAt.</summary>
        public DateTime WhenUtc
        {
            get { return PaidAtUtc ?? CreatedAtUtc; }
        }
    }

    /// <summary>Filtros (querystring) + resultados de /Payments/Received.</summary>
    public class ReceivedPageVm
    {
        public const int PageSize = 50;

        public string Range { get; set; }        // today | 7d | month | all | custom
        public string From { get; set; }         // yyyy-MM-dd (AST), solo custom
        public string To { get; set; }           // yyyy-MM-dd (AST), inclusive, solo custom
        public string Q { get; set; }
        public int Page { get; set; }

        public DateTime? FromUtc { get; set; }   // incluido
        public DateTime? ToUtc { get; set; }     // excluido
        public string RangeLabel { get; set; }
        public string Error { get; set; }

        public int TotalCount { get; set; }
        public decimal TotalAmount { get; set; }
        public List<ReceivedPaymentRow> Rows { get; set; }

        public int PageCount
        {
            get { return Math.Max(1, (TotalCount + PageSize - 1) / PageSize); }
        }

        public ReceivedPageVm()
        {
            Rows = new List<ReceivedPaymentRow>();
            Range = "month";
            Page = 1;
        }
    }
}
