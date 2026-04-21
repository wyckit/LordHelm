# LordHelm.Skills

**Purpose.** The Skill Library. Owns the XML+JSON hybrid manifest format, canonicalization, validation, local persistence (SQLite), hot-reload (FileSystemWatcher), and the JIT transpiler that turns a manifest + args into a shell-specific CLI invocation.

Depends on: `LordHelm.Core`, `McpEngramMemory.Core` (for optional engram mirroring), `NJsonSchema`, `Microsoft.Data.Sqlite`, `System.Security.Cryptography.Xml`.

## Manifest lifecycle

```
skills/*.skill.xml
        │
        ▼   (FileSystemWatcher / startup scan)
   SkillLoader ──► ManifestValidator ──► SkillCanonicalizer ──► SkillManifestParser
                         │                      │                      │
                         ▼                      ▼                      ▼
                     XSD pass              SHA-256 hash           AST record
                     JSON Schema          (W3C C14N 1.0 +
                     Draft 2020-12        compact JSON CDATA)
                         │                      │                      │
                         └──────────────────────┴──────────────────────┘
                                                 │
                                                 ▼
                                         SqliteSkillCache (primary)
                                                 │
                                                 ▼
                                      IEngramClient (mirror, non-fatal)
```

## Public types

### Schema + validation
- `SkillManifestSchema.Schemas` — lazy-compiled `XmlSchemaSet` from the embedded `Schema/skill-manifest.xsd`. One `targetNamespace`: `https://lordhelm.dev/schemas/skill-manifest/v1`.
- `ValidationStage` — `Xsd` or `JsonSchema`. Which gate produced an error.
- `ValidationError`, `ValidationReport` — structured validation results. Never thrown; always returned.
- `ManifestValidator.Validate(string rawXml)` — two-stage gate. XSD first; only if XSD passes, extract CDATA and validate it as JSON Schema Draft 2020-12.

### Canonicalization + parsing
- `SkillCanonicalizer.Canonicalize(string rawXml)` — **load-bearing**. Parses with `PreserveWhitespace=false`, compacts the embedded JSON Schema CDATA, applies Exclusive C14N 1.0 via `XmlDsigExcC14NTransform`, returns `Canonical { Bytes, Sha256Hex, CanonicalXml }`. Changing this algorithm invalidates every stored hash.
- `SkillManifestParser.Parse(string rawXml)` — runs the canonicalizer then deserializes into a `SkillManifest`. `ContentHashSha256` is set from the canonical form.

### Persistence + loading
- `ISkillCache` — `InitializeAsync`, `HasHashAsync`, `UpsertAsync`, `GetByIdAsync`, `ListAsync`, `RemoveByFilePathAsync`. SQLite schema: single `skills` table keyed by `content_hash`, indexed by `skill_id` and `file_path`.
- `SqliteSkillCache` — default implementation. Transactional upsert-by-file-path. Never invalidates by timestamp — only by content hash.
- `ISkillLoader` / `SkillLoader` — scans a directory, validates each file, hashes, skips unchanged, upserts new. Returns `LoadResult { TotalFiles, Loaded, SkippedUnchanged, Invalid }`.
- `SkillFileWatcher` — FileSystemWatcher with 400 ms per-file debounce. Swallows watcher-start errors and logs a warning; the loader still works in startup-scan-only mode.

### Transpilation
- `ShellEscaper.Escape(string value, TargetShell shell)` — bash single-quote wrapping, pwsh double-quote with backtick escapes, cmd doubled quotes.
- `FlagMappingTable` — per-vendor canonical-name → CLI-flag lookup keyed by `(vendor, version, canonical)` with `*` version fallback. `Default()` ships mappings for claude, gemini, codex.
- `TranspiledInvocation` — `Executable`, `Arguments`, `Env`.
- `IJitTranspiler.Transpile(skill, args, vendorId, cliVersion, shell)` — returns a cached invocation. LRU keyed by `(skillHash, vendor, version, shell)`.
- `JitTranspiler` — default impl; implements `ITranspilerCacheInvalidator` so the Scout subsystem can call `Invalidate(vendor, version)` when capability drift is detected.
- `ITranspilerCacheInvalidator` — narrow seam exposed to `LordHelm.Scout` so it can drop stale entries without a project reference to the full transpiler.

## Collaborators

- **`LordHelm.Scout`** — on `MutationEvent`, calls `ITranspilerCacheInvalidator.Invalidate` to drop entries whose `cliVersion` just changed.
- **`LordHelm.Execution`** — `ExecutionRouter` asks `IJitTranspiler` to produce the invocation before dispatching to the Host or Sandbox runner.
- **`LordHelm.Orchestrator`** — `DefaultExpertProvisioner` resolves manifests from `ISkillCache` to build an `ExpertProfile` + `ExpertRunner`.
- **`LordHelm.Web`** — `SkillStartupLoader` in Program.cs drives `ISkillLoader` once at boot and mirrors each valid manifest to engram via `IEngramClient`.
