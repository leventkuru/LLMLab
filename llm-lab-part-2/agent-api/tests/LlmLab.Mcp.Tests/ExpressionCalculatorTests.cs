using LlmLab.Mcp.Tools;

namespace LlmLab.Mcp.Tests;

public class ExpressionCalculatorTests
{
    [Theory]
    [InlineData("2+3*4", 14)]            // çarpma önce
    [InlineData("(2+3)*4", 20)]
    [InlineData("-2^2", -4)]             // üs, tekli eksiden önce
    [InlineData("2^3^2", 512)]           // sağdan birleşir: 2^(3^2)
    [InlineData("10/4", 2.5)]
    [InlineData("10 % 4", 2)]
    [InlineData("1,5*2", 3)]             // ondalık ayırıcı virgül
    [InlineData("12.5 + 3", 15.5)]
    [InlineData("sqrt(16)+abs(-3)", 7)]
    [InlineData("2 × 3 ÷ 6", 1)]
    [InlineData("--5", 5)]
    public void Evaluates_valid_expressions(string expression, double expected)
    {
        Assert.True(ExpressionCalculator.TryEvaluate(expression, out var result, out var error), error);
        Assert.Equal(expected, result, 10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2+")]
    [InlineData("(2+3")]
    [InlineData("2 3")]
    [InlineData("1/0")]
    [InlineData("5 % 0")]
    [InlineData("sqrt(-1)")]
    [InlineData("foo(2)")]
    [InlineData("1.2.3")]
    [InlineData("10^1000")]              // sonsuz
    [InlineData("System.Environment.Exit(0)")] // eval yok: tanınmayan her şey reddedilir
    public void Rejects_invalid_or_dangerous_input_with_a_reason(string expression)
    {
        Assert.False(ExpressionCalculator.TryEvaluate(expression, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_overlong_and_too_deep_expressions()
    {
        Assert.False(ExpressionCalculator.TryEvaluate(new string('1', ExpressionCalculator.MaxLength + 1), out _, out _));
        // 90 + 1 + 90 = 181 karakter: uzunluk sınırının altında, derinlik sınırının (64) üstünde.
        Assert.False(ExpressionCalculator.TryEvaluate(new string('(', 90) + "1" + new string(')', 90), out _, out var error));
        Assert.Contains("derin", error);
    }

    [Fact]
    public void Describe_returns_the_expression_with_its_result_or_a_failure_message()
    {
        Assert.Equal("2+2*3 = 8", ExpressionCalculator.Describe("2+2*3"));
        Assert.StartsWith("Hesaplanamadı:", ExpressionCalculator.Describe("1/0"));
    }
}
