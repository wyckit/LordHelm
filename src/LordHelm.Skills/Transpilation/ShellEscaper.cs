using LordHelm.Core;

namespace LordHelm.Skills.Transpilation;

public static class ShellEscaper
{
    public static string Escape(string value, TargetShell shell) => shell switch
    {
        TargetShell.Bash => BashEscape(value),
        TargetShell.PowerShell => PwshEscape(value),
        TargetShell.Cmd => CmdEscape(value),
        _ => throw new ArgumentOutOfRangeException(nameof(shell)),
    };

    private static string BashEscape(string v)
    {
        if (v.Length == 0) return "''";
        if (v.All(IsBashSafe)) return v;
        return "'" + v.Replace("'", "'\\''") + "'";
    }

    private static string PwshEscape(string v)
    {
        var escaped = v.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");
        return "\"" + escaped + "\"";
    }

    private static string CmdEscape(string v)
    {
        var escaped = v.Replace("\"", "\"\"");
        return "\"" + escaped + "\"";
    }

    private static bool IsBashSafe(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '=' or ',';
}
