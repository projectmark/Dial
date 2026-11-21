# Chassis Repository Guidelines & Rules

## 1. Single Master Blueprint Constraint (MANDATORY)

- **Single Master Blueprint**: `REFERENCE_CHASSIS.md` is the sole, binding architectural and Fluent 2 blueprint. Target projects and skills use exclusively this single document without translation folders (`languages/`).
- **Single-Read AI Tool Limit Constraint**:
  - `REFERENCE_CHASSIS.md` must strictly remain:
    - Under **46,080 bytes**
    - Under **800 lines**
  - This ensures Antigravity 2.0 can ingest the complete blueprint in a single `read_file` tool call without chunking or truncation.

---

## 2. Project Architecture & Design Rules

- **Zero-Hardcoding**: Never hardcode application names, mutex keys, storage paths, or versions. Metadata must be resolved dynamically at runtime from `Assembly.GetEntryAssembly()` via `AppVersion`.
- **Zero-XAML Backend**: Keep `Chassis.Core` 100% free of XAML dependencies. All domain features must implement `IChassisModule`.
- **Fluent 2 Standards**: Adhere strictly to the Microsoft Fluent 2 material hierarchy (Layer 0 `Mica Alt`, Layer 1 `Card Fill` with `1px` surface stroke without DropShadow, Layer 2 `In-App Acrylic`), 5 control interaction states, 4px spatial grid, and Zero Layout Shift rules.
- **Verification**: Run `.\scripts\verify-blueprints.ps1` before committing blueprint changes to verify size constraints.
- **Skill & Multi-Repo Synchronization**: Maintain the Antigravity skill in `.agents/skills/chassis/` and keep global (`~/.gemini/config/plugins/chassis/`) and external repositories updated via `.\scripts\sync-chassis-skill.ps1`.
- **Release Automation**: Build test releases and standalone single-file executables via `.\scripts\build-release.ps1` into `releases/v<Version>/` and `releases/latest/` with SHA256 checksums and `releases.json` manifests.
- **Git & Commit Discipline**: Ensure every Chassis repository is initialized (`git init` + `.gitignore`) and commit cleanly after every logical change or phase (push only on request).
