using System.ComponentModel.DataAnnotations;

namespace LlmLab.Infrastructure.Ai;

/// <summary>Ollama bağlantısı ("Ollama" bölümü).</summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Konteynerde Ollama "localhost"ta değil Windows ana makinesinde olduğundan host.docker.internal verilir.</summary>
    [Required, Url] public string BaseUrl { get; set; } = "http://localhost:11434";

    [Required] public string ChatModel { get; set; } = "qwen3:8b";

    /// <summary>
    /// "qwen3-embed-cpu": qwen3-embedding:0.6b ile aynı ağırlıklar, ama CPU'da çalışan varyant (ollama/Modelfile.qwen3-embed-cpu).
    /// 8 GB'lık kartta embedding'i GPU'ya almak sohbet hızını 42 -> 6-9 token/sn'ye düşürür.
    /// </summary>
    [Required] public string EmbeddingModel { get; set; } = "qwen3-embed-cpu";
}
