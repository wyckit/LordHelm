using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LordHelm.Core;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;

namespace LordHelm.Execution;

/// <summary>
/// In-process C# evaluation via Roslyn (<c>Microsoft.CodeAnalysis.CSharp.Scripting</c>).
/// Binds to the <c>csharp-scripting</c> skill. Unlike <c>execute-csharp</c> which
/// runs in a Docker sandbox, this handler evaluates the script in the Web host
/// process so it can introspect the live DI graph via <c>globals.Services</c>.
///
/// Security model: the skill is Host tier / Exec risk, so the operator confirms
/// every invocation through the approval gate. The handler does not drop caps
/// or sandbox the code — the operator is trusting the script author.
///
/// Script sees a <see cref="ScriptGlobals"/> object as <c>globals</c>:
///   <c>globals.Services</c>   → <see cref="IServiceProvider"/> (pull anything in DI)
///   <c>globals.CallerId</c>   → the expert id that dispatched the script
///   <c>globals.WriteLine(s)</c> / <c>globals.Write(s)</c> → captured output
/// </summary>
public sealed class CSharpScriptHostHandler : IHostSkillHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CSharpScriptHostHandler> _logger;
    private readonly ScriptOptions _options;

    public CSharpScriptHostHandler(IServiceProvider services, ILogger<CSharpScriptHostHandler> logger)
    {
        _services = services;
        _logger = logger;
        _options = ScriptOptions.Default
            .WithReferences(
                typeof(object).Assembly,
                typeof(System.Linq.Enumerable).Assembly,
                typeof(System.Collections.Generic.List<>).Assembly,
                typeof(System.Text.Json.JsonDocument).Assembly,
                typeof(IServiceProvider).Assembly,
                typeof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions).Assembly,
                typeof(SkillManifest).Assembly,
                typeof(ScriptGlobals).Assembly)
            .WithImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "System.Text",
                "System.Text.Json",
                "System.Threading.Tasks",
                "Microsoft.Extensions.DependencyInjection",
                "LordHelm.Core",
                "LordHelm.Execution");
    }

    public bool Handles(string skillId) =>
        string.Equals(skillId, "csharp-scripting", StringComparison.OrdinalIgnoreCase);

    public async Task<HostInvocationResult> RunAsync(
        SkillManifest skill, JsonDocument args, ExpertProfile caller, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!args.RootElement.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
        {
            return new HostInvocationResult(
                ExitCode: 2,
                Stdout: string.Empty,
                Stderr: "csharp-scripting: missing required 'code' string argument",
                Elapsed: sw.Elapsed);
        }
        var code = codeEl.GetString() ?? string.Empty;

        var globals = new ScriptGlobals(_services, caller.ExpertId);
        try
        {
            _logger.LogInformation("Running csharp-scripting ({Chars} chars) for {Caller}", code.Length, caller.ExpertId);
            var result = await CSharpScript.EvaluateAsync(code, _options, globals: globals, cancellationToken: ct);
            if (result is not null)
            {
                globals.WriteLine(Convert.ToString(result) ?? string.Empty);
            }
            return new HostInvocationResult(
                ExitCode: 0,
                Stdout: globals.CapturedOutput,
                Stderr: string.Empty,
                Elapsed: sw.Elapsed);
        }
        catch (CompilationErrorException cex)
        {
            return new HostInvocationResult(
                ExitCode: 1,
                Stdout: globals.CapturedOutput,
                Stderr: "compile error:\n  " + string.Join("\n  ", cex.Diagnostics),
                Elapsed: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new HostInvocationResult(
                ExitCode: 1,
                Stdout: globals.CapturedOutput,
                Stderr: $"{ex.GetType().Name}: {ex.Message}",
                Elapsed: sw.Elapsed);
        }
    }
}

/// <summary>
/// Exposed to the script as <c>globals</c>. Keep the surface small and
/// documented — operators read this and wonder what they can call.
/// </summary>
public sealed class ScriptGlobals
{
    private readonly StringBuilder _out = new();

    public IServiceProvider Services { get; }
    public string CallerId { get; }

    public ScriptGlobals(IServiceProvider services, string callerId)
    {
        Services = services;
        CallerId = callerId;
    }

    public void Write(string s) => _out.Append(s);
    public void WriteLine(string s) => _out.AppendLine(s);
    public void WriteLine() => _out.AppendLine();

    public string CapturedOutput => _out.ToString();
}
