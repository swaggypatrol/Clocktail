namespace Bwt.Gpu;

/// <summary>Preparation failed its end-to-end warm-up; the original batch status and evidence remain available.</summary>
public sealed class WorkloadPreparationException(string message, BatchResult result) : InvalidOperationException(message)
{
    public BatchResult Result { get; } = result;
}
