using System.Globalization;

namespace LlmLab.Mcp.Tools;

/// <summary>
/// `calculate` aracının hesaplayıcısı. Küçük bir özyinelemeli çözümleyicidir: kod çalıştırmaz, serbest ifade
/// değerlendirmez (eval yok); yalnızca sayı, + - * / % ^, parantez ve sqrt/abs tanır. Modeller aritmetikte hata yapar,
/// bu yüzden hesap modele değil buraya yaptırılır.
/// </summary>
public static class ExpressionCalculator
{
    public const int MaxLength = 200;
    private const int MaxDepth = 64;

    /// <summary>İfadeyi hesaplar. Başarısızsa <paramref name="error"/> kullanıcıya/modele gösterilecek kısa bir nedendir.</summary>
    public static bool TryEvaluate(string? expression, out double result, out string? error)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "İfade boş.";
            return false;
        }
        if (expression.Length > MaxLength)
        {
            error = $"İfade en fazla {MaxLength} karakter olabilir.";
            return false;
        }

        try
        {
            var parser = new Parser(expression);
            result = parser.Parse();
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                error = "Sonuç tanımsız ya da çok büyük.";
                return false;
            }
            error = null;
            return true;
        }
        catch (CalculationException e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Modele döndürülen metin: ifade ve sonuç ya da hata.</summary>
    public static string Describe(string? expression)
    {
        if (!TryEvaluate(expression, out var value, out var error)) return $"Hesaplanamadı: {error}";
        return $"{expression!.Trim()} = {value.ToString("G15", CultureInfo.InvariantCulture)}";
    }

    private sealed class CalculationException(string message) : Exception(message);

    private sealed class Parser(string text)
    {
        private int _pos;
        private int _depth;

        public double Parse()
        {
            var value = Expression();
            SkipSpaces();
            if (_pos < text.Length) throw new CalculationException($"Beklenmeyen karakter: '{text[_pos]}'.");
            return value;
        }

        // expr := term (('+' | '-') term)*
        private double Expression()
        {
            Enter();
            var value = Term();
            while (true)
            {
                SkipSpaces();
                if (Match('+')) value += Term();
                else if (Match('-')) value -= Term();
                else break;
            }
            _depth--;
            return value;
        }

        // term := unary (('*' | '/' | '%') unary)*
        private double Term()
        {
            var value = Unary();
            while (true)
            {
                SkipSpaces();
                if (Match('*') || Match('×')) value *= Unary();
                else if (Match('/') || Match('÷'))
                {
                    var divisor = Unary();
                    if (divisor == 0) throw new CalculationException("Sıfıra bölme.");
                    value /= divisor;
                }
                else if (Match('%'))
                {
                    var divisor = Unary();
                    if (divisor == 0) throw new CalculationException("Sıfıra bölme.");
                    value %= divisor;
                }
                else break;
            }
            return value;
        }

        // unary := ('+' | '-') unary | power   (-2^2 = -(2^2))
        private double Unary()
        {
            SkipSpaces();
            if (Match('-')) return -Unary();
            if (Match('+')) return Unary();
            return Power();
        }

        // power := primary ('^' unary)?   (sağdan birleşir: 2^3^2 = 2^(3^2))
        private double Power()
        {
            var baseValue = Primary();
            SkipSpaces();
            if (!Match('^')) return baseValue;
            Enter();
            var exponent = Unary();
            _depth--;
            return Math.Pow(baseValue, exponent);
        }

        // primary := number | '(' expr ')' | name '(' expr ')'
        private double Primary()
        {
            SkipSpaces();
            if (_pos >= text.Length) throw new CalculationException("İfade beklenmedik şekilde bitti.");

            if (Match('('))
            {
                var inner = Expression();
                SkipSpaces();
                if (!Match(')')) throw new CalculationException("Kapanmamış parantez.");
                return inner;
            }

            if (char.IsLetter(text[_pos]))
            {
                var start = _pos;
                while (_pos < text.Length && char.IsLetter(text[_pos])) _pos++;
                var name = text[start.._pos].ToLowerInvariant();
                SkipSpaces();
                if (!Match('(')) throw new CalculationException($"'{name}' için parantez bekleniyordu.");
                var argument = Expression();
                SkipSpaces();
                if (!Match(')')) throw new CalculationException("Kapanmamış parantez.");
                return name switch
                {
                    "sqrt" when argument < 0 => throw new CalculationException("Negatif sayının karekökü alınamaz."),
                    "sqrt" => Math.Sqrt(argument),
                    "abs" => Math.Abs(argument),
                    _ => throw new CalculationException($"Bilinmeyen işlev: '{name}'. Yalnızca sqrt ve abs desteklenir."),
                };
            }

            return Number();
        }

        // Ondalık ayırıcı '.' ya da ','; binlik ayırıcı desteklenmez.
        private double Number()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] is '.' or ','))
            {
                _pos++;
            }
            if (start == _pos) throw new CalculationException($"Beklenmeyen karakter: '{text[_pos]}'.");

            var literal = text[start.._pos].Replace(',', '.');
            if (!double.TryParse(literal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                throw new CalculationException($"Geçersiz sayı: '{text[start.._pos]}'.");
            }
            return value;
        }

        private bool Match(char c)
        {
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw new CalculationException("İfade çok derin iç içe.");
        }
    }
}
