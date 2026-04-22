using LordHelm.Core;

namespace LordHelm.Web;

/// <summary>
/// Forwards <see cref="IGoalProgressSink"/> events into <see cref="WidgetState"/> so
/// every task inside a running goal surfaces as its own <c>Task</c> widget on the grid
/// and updates live as the task runs.
/// </summary>
public sealed class WidgetGoalProgressSink : IGoalProgressSink
{
    private readonly WidgetState _state;
    public WidgetGoalProgressSink(WidgetState state) { _state = state; }

    public Task OnGoalStartedAsync(string goalId, string goal, int plannedTaskCount, CancellationToken ct = default)
    {
        _state.StartTaskWidget(goalId, "goal", "goal: " + goal);
        // plannedTaskCount is 0 here because decomposition happens inside the
        // manager AFTER this callback fires. The real task count materialises
        // as each sub-task widget spawns on the grid.
        _state.AppendTaskLog(goalId, "goal", "dispatching...");
        return Task.CompletedTask;
    }

    public Task OnTaskStartedAsync(string goalId, string taskId, string taskGoal, CancellationToken ct = default)
    {
        _state.StartTaskWidget(goalId, taskId, taskGoal);
        return Task.CompletedTask;
    }

    public Task OnTaskLogAsync(string goalId, string taskId, string line, CancellationToken ct = default)
    {
        _state.AppendTaskLog(goalId, taskId, line);
        return Task.CompletedTask;
    }

    public Task OnTaskCompletedAsync(string goalId, string taskId, bool succeeded, string? output, CancellationToken ct = default)
    {
        _state.CompleteTaskWidget(goalId, taskId, succeeded, output);
        return Task.CompletedTask;
    }

    public Task OnGoalCompletedAsync(string goalId, bool succeeded, string? errorDetail, CancellationToken ct = default)
    {
        _state.CompleteTaskWidget(goalId, "goal", succeeded, errorDetail ?? (succeeded ? "ok" : "failed"));
        return Task.CompletedTask;
    }
}
