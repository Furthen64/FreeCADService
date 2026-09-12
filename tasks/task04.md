# Task 04 — Command-line options can never override environment

## Status
DONE

## Verification
`Program.cs` now adds the normalized `FCADSERVE_*` in-memory collection BEFORE
`AddCommandLine(args)`, so CLI wins over env. Two related fixes landed while
verifying: `ConfigKey.Normalize` was splitting nested keys to `JobsTimeoutSeconds`
instead of the binder's `Jobs:TimeoutSeconds` (unit-tested in `ConfigKeyTests`),
and the command-line provider ignores the colon value separator — the documented
(`README`) and e2e-proven form is `--Key=Value` (`--Port=...`, or
`--Jobs:TimeoutSeconds=...`). `tests/integration.sh` starts the service with an
env `FCADSERVE_PORT` and a conflicting `--Port=` and asserts the CLI port
answers while the env port does not.

## Context
`Program.cs` builds configuration as:

```csharp
Configuration.AddJsonFile("fcadserve.json", optional: true)
    .AddEnvironmentVariables("FCADSERVE_")
    .AddCommandLine(args);
```

...then iterates `AsEnumerable()` (which respects precedence: CLI > env > json),
normalizes env keys, and re-adds them with `AddInMemoryCollection(normalized)`.
The in-memory provider is appended LAST, so it has the HIGHEST precedence —
the normalized copies of `FCADSERVE_*` values now shadow any `--Key=value`
command-line argument. CLI overrides are silently dead.

## Scope
- `src/fcadserve/Program.cs` (config assembly, `NormalizeConfigKey`)

## Requirements
Preserve intended precedence: `fcadserve.json` < env < command line.
Build the normalized collection from the ORIGINAL env provider only (not the
merged `AsEnumerable()`), or add the normalized provider BEFORE `AddCommandLine`,
or otherwise ensure CLI wins.

## Acceptance criteria
- Starting the service with `--Jobs__TimeoutSeconds=5` overrides
  `FCADSERVE_JOBS__TIMEOUT_SECONDS`.
- Existing env-only workflows (`launch_server.sh`) behave identically.
- Config from `fcadserve.json` still works and is the lowest precedence.