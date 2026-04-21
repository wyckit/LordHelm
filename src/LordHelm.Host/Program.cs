using System.Net.Http.Json;
using LordHelm.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

// Subcommand dispatcher. Default (no args) = health check.
//   <no args>      — run startup health checks (what the scripts call)
//   check-cli      — deep functional probe of every provider CLI
//   goal "..."     — POST a goal to the running Web API and stream the response
string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "health";

var banner = new FigletText("Lord Helm").Color(Color.Gold1);
AnsiConsole.Write(banner);
AnsiConsole.MarkupLine("[grey]one ring to rule them all[/]");
AnsiConsole.WriteLine();

switch (cmd)
{
    case "health":
    case "--health":
        return await RunHealthAsync();
    case "check-cli":
    case "cli-check":
        return await RunCliFunctionalAsync();
    case "goal":
        return await RunGoalAsync(args.Skip(1).ToArray());
    case "help":
    case "--help":
    case "-h":
        PrintUsage();
        return 0;
    default:
        AnsiConsole.MarkupLine($"[red]Unknown command: {Markup.Escape(cmd)}[/]");
        PrintUsage();
        return 2;
}

static void PrintUsage()
{
    AnsiConsole.MarkupLine("Usage:");
    AnsiConsole.MarkupLine("  [yellow]dotnet run --project src/LordHelm.Host[/]               — run startup health check");
    AnsiConsole.MarkupLine("  [yellow]dotnet run --project src/LordHelm.Host -- check-cli[/]  — functional test every provider CLI");
    AnsiConsole.MarkupLine("  [yellow]dotnet run --project src/LordHelm.Host -- goal \"...\"[/] — submit a goal to the running Web API");
}

static async Task<int> RunHealthAsync()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
    builder.Services.AddSingleton<StartupHealthChecks>();
    using var host = builder.Build();

    var checks = host.Services.GetRequiredService<StartupHealthChecks>();
    var report = await checks.RunAsync(CancellationToken.None);
    report.Render();

    if (!report.AllCritical)
    {
        AnsiConsole.MarkupLine("[red]Critical health check failed.[/]");
        return 1;
    }
    AnsiConsole.MarkupLine("[green]All critical prerequisites present.[/]");
    return 0;
}

static async Task<int> RunCliFunctionalAsync()
{
    AnsiConsole.MarkupLine("[cyan]Probing each provider CLI with a trivial JSON prompt...[/]");
    AnsiConsole.WriteLine();
    var probes = CliFunctionalProbes.Defaults();
    var results = await CliFunctionalProbes.RunAllAsync(probes, TimeSpan.FromSeconds(60));
    CliFunctionalProbes.Render(results);
    AnsiConsole.WriteLine();
    return results.All(r => r.GenerateOk) ? 0 : 1;
}

static async Task<int> RunGoalAsync(string[] rest)
{
    // Parse --model <id>, --vendor <name>, --tier <fast|deep|code>, then the goal string.
    string? model = null, vendor = null, tier = null;
    var goalWords = new List<string>();
    for (int i = 0; i < rest.Length; i++)
    {
        var a = rest[i];
        if (a == "--model" && i + 1 < rest.Length) { model = rest[++i]; }
        else if (a == "--vendor" && i + 1 < rest.Length) { vendor = rest[++i]; }
        else if (a == "--tier" && i + 1 < rest.Length) { tier = rest[++i]; }
        else goalWords.Add(a);
    }
    if (goalWords.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]goal: requires a goal string.[/]");
        AnsiConsole.MarkupLine("  example: [yellow]dotnet run --project src/LordHelm.Host -- goal --tier deep \"summarise README.md\"[/]");
        AnsiConsole.MarkupLine("  flags: [yellow]--vendor claude|gemini|codex[/]  [yellow]--model <id>[/]  [yellow]--tier fast|deep|code[/]");
        return 2;
    }
    var goal = string.Join(' ', goalWords);
    var url = Environment.GetEnvironmentVariable("LORDHELM_WEB_URL") ?? "http://localhost:5080";

    AnsiConsole.MarkupLine($"[cyan]Dispatching goal to {url}/api/goals[/]");
    AnsiConsole.MarkupLine($"  goal: [white]{Markup.Escape(goal)}[/]");
    if (vendor is not null) AnsiConsole.MarkupLine($"  vendor: [yellow]{vendor}[/]");
    if (model is not null)  AnsiConsole.MarkupLine($"  model:  [yellow]{model}[/]");
    if (tier is not null)   AnsiConsole.MarkupLine($"  tier:   [yellow]{tier}[/]");
    AnsiConsole.WriteLine();

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    try
    {
        var body = new { goal, preferredVendor = vendor, model, tier };
        var resp = await http.PostAsJsonAsync($"{url}/api/goals", body);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]HTTP {(int)resp.StatusCode}[/]");
            AnsiConsole.WriteLine(json);
            return 1;
        }
        AnsiConsole.MarkupLine("[green]Accepted:[/]");
        AnsiConsole.WriteLine(json);
        return 0;
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine($"[red]Could not reach Web API at {url}: {Markup.Escape(ex.Message)}[/]");
        AnsiConsole.MarkupLine("  is [yellow]dotnet run --project src/LordHelm.Web[/] running?");
        return 1;
    }
}
