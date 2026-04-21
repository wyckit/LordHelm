namespace LordHelm.Core;

/// <summary>
/// Callback surface Orchestrator uses to notify the UI layer about goal progress
/// without taking a dependency on Blazor / Web. The Web layer registers a concrete
/// sink that forwards into the widget state.
/// </summary>
public interface IGoalProgressSink
{
    Task OnGoalStartedAsync(string goalId, string goal, int plannedTaskCount, CancellationToken ct = default);
    Task OnTaskStartedAsync(string goalId, string taskId, string taskGoal, CancellationToken ct = default);
    Task OnTaskLogAsync(string goalId, string taskId, string line, CancellationToken ct = default);
    Task OnTaskCompletedAsync(string goalId, string taskId, bool succeeded, string? output, CancellationToken ct = default);
    Task OnGoalCompletedAsync(string goalId, bool succeeded, string? errorDetail, CancellationToken ct = default);
}

/// <summary>No-op sink used when nothing has registered a real one (e.g. CLI / headless).</summary>
public sealed class NullGoalProgressSink : IGoalProgressSink
{
    public Task OnGoalStartedAsync(string goalId, string goal, int plannedTaskCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task OnTaskStartedAsync(string goalId, string taskId, string taskGoal, CancellationToken ct = default) => Task.CompletedTask;
    public Task OnTaskLogAsync(string goalId, string taskId, string line, CancellationToken ct = default) => Task.CompletedTask;
    public Task OnTaskCompletedAsync(string goalId, string taskId, bool succeeded, string? output, CancellationToken ct = default) => Task.CompletedTask;
    public Task OnGoalCompletedAsync(string goalId, bool succeeded, string? errorDetail, CancellationToken ct = default) => Task.CompletedTask;
}
