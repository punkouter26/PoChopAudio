# NET_RULES

## 1. Governance

- Naming: `Po{Name}` for solution, projects, root namespaces.
- Stack: .NET 10 / C# 15. Packages pinned centrally in `/Directory.Packages.props`.
- Every project sets:
  ```xml
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  ```
- Git: trunk-based on `master`. No feature branches unless asked.
- Domain: no primitive obsession, no magic strings. IDs are `readonly record struct`; closed sets are enums.

## 2. Layout

Max 2 levels under `src/`.

```
/
├── AGENT.md
├── Directory.Packages.props
├── SCRIPTS/setup.ps1
├── src/
│   ├── Po{Name}.API/       # Minimal API, BFF host, storage, feature slices
│   ├── Po{Name}.Client/    # Blazor WASM
│   └── Po{Name}.Shared/    # DTOs, enums, interfaces, validation
└── tests/
    ├── Po{Name}.Unit/          # pure logic, no I/O
    ├── Po{Name}.Integration/   # Azurite / Testcontainers
    ├── Po{Name}.E2EAPI/        # API contract
    └── Po{Name}.E2EUI/         # Playwright, mobile + desktop
```

Slices: endpoints, DTOs, and handlers live together in `Po{Name}.API/Features/{FeatureName}`. Slices never reference each other — shared types go in `Po{Name}.Shared`. The API project serves the WASM client.

## 3. API, Security, BFF

- Endpoints via `IEndpointRouteBuilder` + `MapGroup()`. Docs via `Microsoft.AspNetCore.OpenApi` + Scalar.
- `/health` and `/diag` required. `/diag` masks all secret values.
- No tokens in the browser: WASM talks to the API only through `HttpOnly`, `SameSite=Strict`, secure cookies.
- Entra ID via the `/common` endpoint with a server-side `FallbackPolicy`.
- Propagate `X-Session-ID` and `X-Correlation-ID` on every outbound HTTP call.
- Dev/test auth: `FakeAuthHandler` driven by `X-Fake-User` / `X-Fake-Roles`. **It must throw `InvalidOperationException` if constructed in Production.**

## 4. Blazor WASM UI

- Header: left = branding, center = actions, right = session / logout.
- Mock data active ⇒ persistent "USING MOCK DATA" banner.
- No inline styles. Scoped `.razor.css` + `:root` custom properties for tokens. Light/dark follow the system theme.
- `Virtualize` for long lists; WebGL/Canvas for heavy visuals.
- WCAG 2.2 AA on every interactive element.

## 5. AI, Observability, Performance

- Local AI: model registries with dtype fallback chains, executed browser/worker-native.
- Tests intercept Azure AI pipeline calls with a custom `DelegatingHandler` — zero token spend.
- Logging: `[LoggerMessage]` source generators on hot paths. No interpolation in log calls.
- HTTP resilience via `AddResiliencePipeline`; caching via `HybridCache`.

## 6. Testing, CI/CD, Hygiene

- Coverage targets: Unit 100% · Integration 50% · API E2E 25% · UI E2E 25%.
- Azure: resource group `PoShared` (or `Po{SolutionName}`). System-assigned Managed Identity + Key Vault. No raw connection strings in app settings.
- Post-deploy smoke test asserts: Blazor render tree initializes, `/health` responds, `/diag` returns masked config.
- Purge dead code and orphaned assets continuously. `AGENT.md` is the living architecture doc.
