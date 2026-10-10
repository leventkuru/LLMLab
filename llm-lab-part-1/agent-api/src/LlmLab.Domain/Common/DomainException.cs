namespace LlmLab.Domain.Common;

/// <summary>İş kuralı ihlali. Üst katmanlar bunu 4xx hatasına çevirir.</summary>
public class DomainException(string message) : Exception(message);
