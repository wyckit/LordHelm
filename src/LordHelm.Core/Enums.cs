namespace LordHelm.Core;

public enum ExecutionEnvironment
{
    Host,
    Docker,
    Remote
}

public enum RiskTier
{
    Read,
    Write,
    Delete,
    Network,
    Exec
}

public enum TrustLevel
{
    None,
    Low,
    Medium,
    High,
    Full
}

public enum TargetShell
{
    Bash,
    PowerShell,
    Cmd
}
