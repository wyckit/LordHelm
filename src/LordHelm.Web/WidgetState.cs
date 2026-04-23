using System.Collections.Concurrent;
using LordHelm.Core;
using LordHelm.Execution;
using LordHelm.Monitor;

namespace LordHelm.Web;

public enum WidgetKind { Subprocess, Approval, Incident, Task, ChatGoal }

public sealed record WidgetModel(
    string Id,
    WidgetKind Kind,
    string Label,
    ExecutionEnvironment? Env,
    string Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? Tail = null,
    ApprovalGate.PendingApproval? PendingApproval = null)
{
    /// <summary>
    /// Attention classifier used by the dashboard to group widgets into
    /// Incidents / Approvals / Running / Completed / Failed. Order is the
    /// visual priority order on the page.
    /// </summary>
    public string BucketOf()
    {
        if (Kind == WidgetKind.Approval && PendingApproval is not null) return "Approval";
        return Status switch
        {
            "incident" or "pending-approval" when Kind == WidgetKind.Incident => "Incident",
            "incident" => "Incident",
            "failed" or "denied" => "Failed",
            "completed" or "approved" => "Completed",
            _ => "Running",
        };
    }

    /// <summary>
    /// True when this widget needs operator eyes on it right now (incidents + pending approvals).
    /// The dashboard adds a stronger visual treatment (pulse, accent color) for these.
    /// </summary>
    public bool NeedsAttention() => BucketOf() is "Incident" or "Approval";
}

/// <summary>
/// Thread-safe view-model for the Blazor dashboard. Backend services push into here;
/// the <see cref="OnChanged"/> event drives Blazor re-renders via StateHasChanged.
/// </summary>
public sealed class WidgetState
{
    private readonly ConcurrentDictionary<string, WidgetModel> _widgets = new();
    private readonly ConcurrentDictionary<string, LogRing> _rings = new();

    public event Action? OnChanged;

    public IReadOnlyList<WidgetModel> Snapshot() =>
        _widgets.Values.OrderBy(w => w.UpdatedAt).ToList();

    public void Upsert(WidgetModel w)
    {
        _widgets[w.Id] = w;
        OnChanged?.Invoke();
    }

    public void Remove(string id)
    {
        _widgets.TryRemove(id, out _);
        _rings.TryRemove(id, out _);
        OnChanged?.Invoke();
    }

    public void AppendLog(string widgetId, string line)
    {
        var ring = _rings.GetOrAdd(widgetId, _ => new LogRing());
        ring.Append(line);
        if (_widgets.TryGetValue(widgetId, out var existing))
        {
            Upsert(existing with { Tail = ring.Snapshot(), UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    public void ApplyProcessEvent(ProcessEvent ev)
    {
        var env = ev.Label.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
            ? ExecutionEnvironment.Docker
            : ExecutionEnvironment.Host;
        var status = ev.Kind switch
        {
            ProcessEventKind.Started => "running",
            ProcessEventKind.Exited => ev.ExitCode == 0 ? "completed" : "failed",
            ProcessEventKind.Incident => "incident",
            _ => _widgets.TryGetValue(ev.SubprocessId, out var existing) ? existing.Status : "running",
        };
        var ring = _rings.GetOrAdd(ev.SubprocessId, _ => new LogRing());
        if (!string.IsNullOrEmpty(ev.Line))
        {
            var tag = ev.Kind == ProcessEventKind.Stderr ? "[ERR]" :
                env == ExecutionEnvironment.Docker ? "[SANDBOX]" : "[HOST]";
            ring.Append($"{tag} {ev.Line}");
        }
        Upsert(new WidgetModel(
            Id: ev.SubprocessId,
            Kind: WidgetKind.Subprocess,
            Label: ev.Label,
            Env: env,
            Status: status,
            UpdatedAt: ev.At,
            Tail: ring.Snapshot()));
    }

    /// <summary>
    /// Mint a ChatGoal parent widget when the chat panel dispatches a goal.
    /// Later-landing Task widgets with matching session id collapse under
    /// this parent in the Home dashboard's attention buckets, so operators
    /// see "this thread of work came from the chat" rather than orphan rows.
    /// </summary>
    public void StartChatGoal(string sessionId, string summary)
    {
        var id = $"chat-{sessionId}";
        Upsert(new WidgetModel(
            Id: id,
            Kind: WidgetKind.ChatGoal,
            Label: summary.Length > 60 ? summary.Substring(0, 60) + "…" : summary,
            Env: null,
            Status: "running",
            UpdatedAt: DateTimeOffset.UtcNow,
            Tail: new[] { $"[CHAT] {summary}" }));
    }

    public void CompleteChatGoal(string sessionId, bool succeeded, string? reply)
    {
        var id = $"chat-{sessionId}";
        if (!_widgets.TryGetValue(id, out var w)) return;
        var ring = _rings.GetOrAdd(id, _ => new LogRing());
        if (!string.IsNullOrEmpty(reply)) ring.Append("[REPLY] " + Truncate(reply, 240));
        Upsert(w with
        {
            Status = succeeded ? "completed" : "failed",
            UpdatedAt = DateTimeOffset.UtcNow,
            Tail = ring.Snapshot(),
        });
    }

    public void RegisterPendingApproval(string widgetId, ApprovalGate.PendingApproval pending)
    {
        var req = pending.Request;

        // Expert approvals arrive with SkillId="expert:{id}". Turn that into a
        // human-scannable label and surface the DiffPreview (the truncated
        // expert output) inline so the operator can decide without drilling in.
        var isExpert = req.SkillId.StartsWith("expert:", StringComparison.Ordinal);
        var subject = isExpert ? req.SkillId["expert:".Length..] : req.SkillId;
        var label = isExpert
            ? $"expert approval: {subject}"
            : $"approval: {subject}";

        var tail = new List<string>
        {
            $"[APPROVAL] tier={req.RiskTier} operator={req.OperatorId}",
            $"[APPROVAL] summary: {req.Summary}",
        };
        if (!string.IsNullOrWhiteSpace(req.DiffPreview))
        {
            tail.Add(isExpert ? "[APPROVAL] output preview:" : "[APPROVAL] diff:");
            foreach (var line in req.DiffPreview.Split('\n', 8))
                tail.Add("  " + line.TrimEnd());
        }

        Upsert(new WidgetModel(
            Id: widgetId,
            Kind: WidgetKind.Approval,
            Label: label,
            Env: null,
            Status: "pending-approval",
            UpdatedAt: DateTimeOffset.UtcNow,
            Tail: tail,
            PendingApproval: pending));
    }

    public void ResolveApproval(string widgetId, bool approved, string reason, ApprovalGate gate)
    {
        if (!_widgets.TryGetValue(widgetId, out var w) || w.PendingApproval is null) return;
        gate.Resolve(w.PendingApproval, approved, reason);
        Upsert(w with
        {
            Status = approved ? "approved" : "denied",
            UpdatedAt = DateTimeOffset.UtcNow,
            PendingApproval = null,
        });
    }

    // ---------- goal-run integration ----------

    public void StartTaskWidget(string goalId, string taskId, string label)
    {
        var widgetId = WidgetIdFor(goalId, taskId);
        Upsert(new WidgetModel(
            Id: widgetId,
            Kind: WidgetKind.Task,
            Label: $"{goalId[..Math.Min(12, goalId.Length)]}: {label}",
            Env: ExecutionEnvironment.Host,
            Status: "running",
            UpdatedAt: DateTimeOffset.UtcNow,
            Tail: Array.Empty<string>()));
    }

    public void AppendTaskLog(string goalId, string taskId, string line)
        => AppendLog(WidgetIdFor(goalId, taskId), $"[TASK] {line}");

    public void CompleteTaskWidget(string goalId, string taskId, bool succeeded, string? output)
    {
        var widgetId = WidgetIdFor(goalId, taskId);
        if (!_widgets.TryGetValue(widgetId, out var w)) return;
        var ring = _rings.GetOrAdd(widgetId, _ => new LogRing());
        if (!string.IsNullOrEmpty(output)) ring.Append($"[RESULT] {Truncate(output, 200)}");
        Upsert(w with
        {
            Status = succeeded ? "completed" : "failed",
            UpdatedAt = DateTimeOffset.UtcNow,
            Tail = ring.Snapshot(),
        });
    }

    public static string WidgetIdFor(string goalId, string taskId) => $"{goalId}::{taskId}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// Create a demo widget from the UI. Spawns a fake subprocess that emits log
    /// lines for ~20 seconds so the grid has something to render without a
    /// real backend wired up.
    /// </summary>
    public void SpawnDemo(string id, string label, ExecutionEnvironment env)
    {
        Upsert(new WidgetModel(id, WidgetKind.Subprocess, label, env, "running", DateTimeOffset.UtcNow, Array.Empty<string>()));
        _ = Task.Run(async () =>
        {
            var rng = new Random();
            var steps = new[]
            {
                "initialising expert loadout",
                "acquired skill: read-file v0.1.0",
                "engram recall: 3 hits in lord_helm_skills",
                "transpiled invocation for claude v2.1.0",
                "stdout: 128 bytes",
                "stdout: 512 bytes",
                "stdout: 1024 bytes",
                "cpu: 14.2%  rss: 48MB",
                "completed skill invocation",
                "writing result node to engram",
            };
            for (int i = 0; i < steps.Length; i++)
            {
                await Task.Delay(rng.Next(600, 1400));
                AppendLog(id, steps[i]);
            }
            if (_widgets.TryGetValue(id, out var w))
            {
                Upsert(w with { Status = "completed", UpdatedAt = DateTimeOffset.UtcNow });
            }
        });
    }
}
