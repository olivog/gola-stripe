using System;

namespace GolaStripe.Helpers
{
    /// <summary>
    /// AST = hora de Puerto Rico / La Paz: UTC-4 fijo, sin horario de verano. La DB guarda UTC.
    /// </summary>
    public static class Ast
    {
        public static readonly TimeSpan Offset = TimeSpan.FromHours(-4);

        public static DateTime UtcToAst(DateTime utc)
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) + Offset;
        }

        /// <summary>Hora AST (p. ej. medianoche de un día) → UTC para comparar con la DB.</summary>
        public static DateTime AstToUtc(DateTime ast)
        {
            return DateTime.SpecifyKind(ast - Offset, DateTimeKind.Utc);
        }

        public static DateTime NowAst
        {
            get { return UtcToAst(DateTime.UtcNow); }
        }

        public static DateTime TodayAst
        {
            get { return NowAst.Date; }
        }
    }
}
