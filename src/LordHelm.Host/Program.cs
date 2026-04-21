using LordHelm.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

var banner = new FigletText("Lord Helm").Color(Color.Gold1);
AnsiConsole.Write(banner);
AnsiConsole.MarkupLine("[grey]one ring to rule them all - v0 scaffold[/]");
AnsiConsole.WriteLine();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddSingleton<StartupHealthChecks>();

using var host = builder.Build();

var checks = host.Services.GetRequiredService<StartupHealthChecks>();
var report = await checks.RunAsync(CancellationToken.None);
report.Render();

if (!report.AllCritical)
{
    AnsiConsole.MarkupLine("[red]Critical health check failed - aborting.[/]");
    return 1;
}

AnsiConsole.MarkupLine("[green]Scaffold ready. Phase 0 complete.[/]");
return 0;
