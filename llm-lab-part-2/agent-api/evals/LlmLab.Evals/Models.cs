namespace LlmLab.Evals;

/// <summary>Bir vakanın deterministik (kural tabanlı) beklentileri. Hepsi isteğe bağlıdır.</summary>
public sealed record Checks(
    string[]? MustCallTools = null,
    string[]? MustNotCallTools = null,
    string[]? AnswerMustContain = null,
    string[]? AnswerMustNotContain = null,
    string? AnswerMustMatch = null,
    bool? MustHaveSources = null,
    bool? MustHaveNoSources = null,
    int? MaxSeconds = null);

/// <summary>
/// Değerlendirme vakası. Rubric: yargıç (LLM) için "iyi cevap neyi içerir" tarifi; Reference: biliniyorsa doğru cevabın
/// özeti. Deterministik kontroller Checks'te; ikisi birlikte vakanın geçmesini belirler.
/// </summary>
public sealed record EvalCase(
    string Id,
    string Question,
    string? Agent = null,
    bool UseDocuments = false,
    bool Think = false,
    string[]? Tags = null,
    Checks? Checks = null,
    string? Rubric = null,
    string? Reference = null);

public sealed record Observed(
    string Answer, IReadOnlyList<string> Tools, int Sources, IReadOnlyList<string> SourceTexts,
    double Seconds, double? FirstTokenSeconds, string? Error);

public sealed record CheckResult(string Name, bool Passed, string? Detail = null);

public sealed record Judgement(int Correctness, int Groundedness, int Completeness, string Reason)
{
    /// <summary>Yargıç eşiği: doğruluk ve dayanak en az 4/5.</summary>
    public bool Passed => Correctness >= 4 && Groundedness >= 4;
}

public sealed record CaseResult(
    string Id, bool Passed, Observed Observed, IReadOnlyList<CheckResult> Checks, Judgement? Judge, string? JudgeError);

public sealed record Report(
    DateTime StartedAt, string ApiBase, string? JudgeModel, int Total, int Passed, double PassRate,
    double? AvgCorrectness, double? AvgGroundedness, double AvgSeconds, IReadOnlyList<CaseResult> Cases);

/// <summary>Regresyon karşılaştırması için saklanan en küçük sonuç.</summary>
public sealed record Baseline(DateTime CreatedAt, double PassRate, Dictionary<string, bool> Cases);
