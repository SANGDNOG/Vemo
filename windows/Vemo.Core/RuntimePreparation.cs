namespace Vemo.Core;

public sealed record RuntimePreparationResult(ModelSelection Models, string Status, bool Ready);
public interface IRuntimePreparation
{
    Task<RuntimePreparationResult> PrepareAsync(ModelSelection models, CancellationToken ct = default);
}
