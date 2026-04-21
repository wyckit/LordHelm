# LordHelm.Host

**Purpose.** The administrative console. Spectre.Console-based entry point for health checks, on-demand Scout runs, and other operational tasks that don't need the full Blazor web surface.

This project is **not** the canonical composition root for production — that role belongs to `LordHelm.Web`. `Host` is where an operator runs diagnostics from a shell: "is my Docker daemon reachable? Are the CLI providers on PATH? Let me force a Scout cycle without spinning up the dashboard."

Depends on: every core project (reference graph), `Spectre.Console`, `Microsoft.Extensions.Hosting`.

## Public types

- `StartupHealthChecks.RunAsync(ct)` — probes `docker version`, `claude --version`, `gemini --version`, `codex --version`. Returns a `HealthReport` with per-probe `HealthResult { Name, Ok, Critical, Detail }`.
- `HealthReport` — rendered as a Spectre table. `AllCritical` is true only when every critical probe succeeded; docker is currently the only critical probe.

## `Program.cs`

Presents the Lord Helm figlet banner, composes a host, runs the health check, prints the result table, and exits with code 0 on success / 1 on critical failure.

## Collaborators

- **`LordHelm.Web`** — the production runtime. `Host` and `Web` share the same set of referenced projects but have intentionally different responsibilities.
- **`scripts/start.ps1`** — invokes `dotnet run --project src/LordHelm.Host` when the user passes `-HealthOnly`.

## Invariants

1. **Host has no hosted services.** All `IHostedService` registrations belong in `LordHelm.Web/Program.cs`.
2. **Health checks are side-effect-free.** Probing must not start Docker, install CLIs, or mutate state.
3. **Host is allowed to exit fast.** Unlike the web app, `Host` runs to completion and returns a nonzero exit code when critical prerequisites fail — useful for CI / scripts.
