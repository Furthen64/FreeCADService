# Task 05 — Subprocess isolation is not what the config implies

## Status
DONE (bubblewrap intentionally not added — documented trust model)

## Verification
- `MaxInputSizeBytes` default lowered 512 MB → 50 MiB (Options/ServiceOptions.cs),
  enforced at submission (`PathPolicy` `input_too_large`, covered by tests) and
  documented in README.
- `FreeCadOptions` doc comment + README Security section now state exactly what
  `flatpak` vs `exec` isolation provides and how `XDG_*` behaves in each mode.
  `exec` is documented as "trust the binary and the host" (no sandbox). A light
  bwrap wrapper was deliberately NOT added: it cannot co-exist with the FreeCAD
  flatpak sandbox and would be cosmetic (same reason the exec mode exists).

## Context
Two gaps:

1. `FreeCadCommandBuilder.ApplyUserConfigIsolation` sets `XDG_CONFIG_HOME` /
   `XDG_CACHE_HOME` / `XDG_DATA_HOME` per-job. Under the DEFAULT `flatpak`
   mode the sandbox largely ignores/owns those variables, so "isolate user
   config" is cosmetic in the mode it ships with. In `exec` mode the env vars
   DO reach the raw process (isolation works) but there is NO sandbox around it.

2. `Mode="exec"` runs the configured FreeCAD command with no containment:
   no bubblewrap, no resource limits, no RAM cap. Combined with
   `MaxInputSizeBytes = 512 MB` (Options/ServiceOptions.cs:91), a single job
   can balloon the host through a 600s FreeCAD session.

GENESIS requires hardening to be documented where it interacts with Flatpak
(GENESIS.md:209-210).

## Scope
- `src/fcadserve/FreeCad/FreeCadCommandBuilder.cs`
- `src/fcadserve/Options/ServiceOptions.cs` (`MaxInputSizeBytes` default,
  isolation options, optional bwrap wrapper)

## Requirements
Either make the isolation genuinely effective, or document accurately. Concretely:

- Lower the default `MaxInputSizeBytes` to a sane FreeCAD mesh limit
  (e.g. 50 MB) and document the knob; keep the option overridable.
- For `exec` mode: either wrap the command in a light sandbox
  (bwrap with read-only root policy + writable state dirs), or add explicit docs
  that `exec` implies trusting the host and the configured binary.
- Add a readiness check / doc note clarifying effective isolation per mode.

## Acceptance criteria
- A 512 MB STL is rejected by default; the configured lower limit is enforced
  both at submission and in the readiness docs.
- Documentation states exactly what isolation `flatpak` vs `exec` provides and
  how `XDG_*` is handled in each.
- Behavior unchanged for the flatpak default beyond the size-limit default.