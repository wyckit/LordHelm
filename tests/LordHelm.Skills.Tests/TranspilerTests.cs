using System.Text.Json;
using LordHelm.Core;
using LordHelm.Skills.Transpilation;

namespace LordHelm.Skills.Tests;

public class TranspilerTests
{
    private static readonly SkillManifest Skill = new(
        Id: "demo",
        Version: new SemVer(1, 0, 0),
        ContentHashSha256: new string('a', 64),
        ExecEnv: ExecutionEnvironment.Host,
        RequiresApproval: false,
        RiskTier: RiskTier.Read,
        Timeout: TimeSpan.FromSeconds(30),
        MinTrust: TrustLevel.Low,
        ParameterSchemaJson: "{}",
        CanonicalXml: "<x/>");

    [Fact]
    public void Maps_Canonical_Parameters_To_Claude_Flags()
    {
        var t = new JitTranspiler();
        using var args = JsonDocument.Parse("""{"model":"claude-opus-4-7","outputFormat":"json","maxTokens":512}""");
        var inv = t.Transpile(Skill, args, "claude", "2.1.0", TargetShell.Bash);

        Assert.Contains("--model", inv.Arguments);
        Assert.Contains("claude-opus-4-7", inv.Arguments);
        Assert.Contains("--output-format", inv.Arguments);
        Assert.Contains("json", inv.Arguments);
        Assert.Contains("--max-tokens", inv.Arguments);
        Assert.Contains("512", inv.Arguments);
    }

    [Fact]
    public void Cache_Hits_On_Repeated_Calls()
    {
        var t = new JitTranspiler();
        using var args = JsonDocument.Parse("""{"model":"m"}""");
        var a = t.Transpile(Skill, args, "claude", "1.0.0", TargetShell.Bash);
        var b = t.Transpile(Skill, args, "claude", "1.0.0", TargetShell.Bash);
        Assert.Same(a, b);
    }

    [Fact]
    public void Invalidate_Drops_Vendor_Version_Cache()
    {
        var t = new JitTranspiler();
        using var args = JsonDocument.Parse("""{"model":"m"}""");
        var a = t.Transpile(Skill, args, "claude", "1.0.0", TargetShell.Bash);
        t.Invalidate("claude", "1.0.0");
        var b = t.Transpile(Skill, args, "claude", "1.0.0", TargetShell.Bash);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void PowerShell_Escape_Handles_Special_Chars()
    {
        var escaped = ShellEscaper.Escape("hello $world \"quoted\"", TargetShell.PowerShell);
        Assert.StartsWith("\"", escaped);
        Assert.Contains("`$world", escaped);
        Assert.Contains("`\"quoted`\"", escaped);
    }

    [Fact]
    public void Bash_Single_Quote_Preserves_Special_Chars()
    {
        var escaped = ShellEscaper.Escape("it's $safe", TargetShell.Bash);
        Assert.Equal("'it'\\''s $safe'", escaped);
    }

    [Fact]
    public void Cmd_Doubles_Internal_Quotes()
    {
        var escaped = ShellEscaper.Escape("say \"hi\"", TargetShell.Cmd);
        Assert.Equal("\"say \"\"hi\"\"\"", escaped);
    }
}
