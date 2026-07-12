# Publishing AVMTradeReporter.Models to NuGet.org

This repository publishes the `AVMTradeReporter.Models` package automatically via the
[`publish-nuget.yml`](.github/workflows/publish-nuget.yml) GitHub Actions workflow.

## When it runs

- Automatically on every push to `master` that touches `AVMTradeReporter.Models/**`
  (or the workflow file itself).
- Manually at any time via **Actions -> Publish NuGet Package -> Run workflow**.

## Versioning scheme

The `<Version>` in [`AVMTradeReporter.Models.csproj`](AVMTradeReporter.Models/AVMTradeReporter.Models.csproj)
(e.g. `1.0.1`) is treated as the base version. The workflow appends the build
timestamp (UTC) as a fourth version component, in `yyyyMMddHH` format (hour
precision):

```
<base version>.<yyyyMMddHH>
```

Example: a base version of `1.0.1` built on 2026-07-12 at 12:00 UTC is published as:

```
1.0.1.2026071212
```

> **Note:** NuGet package versions are parsed as `System.Version`-style numbers,
> and each component must fit in a 32-bit integer (max ~2.1 billion). A full
> `yyyyMMddHHmm` timestamp (minute precision, 12 digits) overflows that limit and
> `dotnet pack`/`dotnet nuget push` reject it outright — that's why the build
> stamp is truncated to hour precision (`yyyyMMddHH`, 10 digits), which stays
> valid until the year 2147. If you ever need multiple publishes within the same
> hour, bump the base `<Version>` in the `.csproj` for that push.

Every build therefore produces a unique, ever-increasing package version without
needing to bump `<Version>` manually for every change. Bump the base `<Version>`
in the `.csproj` only when you want to signal a semantic version change (e.g. a
breaking change bumps the major version).

> **Note:** the workflow's `Build`/`Pack` steps also pin `-p:AssemblyVersion=1.0.0.0
> -p:FileVersion=1.0.0.0`. Those two are separate, older `System.Version` fields
> baked into the compiled DLL, whose components must fit in a `UInt16` (max
> 65535) — much stricter than the package version's Int32-per-component limit
> above. Without pinning them, the build-stamped `<Version>` would overflow that
> and fail to compile with `CS7034`. Pinning them to a stable value also means
> the assembly's own identity doesn't churn on every build (only the NuGet
> package version does), which is the conventional approach.

## One-time setup: NuGet Trusted Publishing (no long-lived secrets)

nuget.org now recommends **Trusted Publishing** over long-lived API keys for
CI/CD publishing (API keys are still supported, but discouraged for automation).
Trusted Publishing works via GitHub's OIDC token: on every run, the workflow
exchanges a short-lived, cryptographically signed GitHub token for a temporary
(1-hour) nuget.org API key — scoped to exactly this repo and workflow file, and
never stored anywhere. This is what [`publish-nuget.yml`](.github/workflows/publish-nuget.yml)
uses via the [`NuGet/login`](https://github.com/NuGet/login) action.

1. **Create a Trusted Publishing policy on nuget.org**
   - Sign in at [nuget.org](https://www.nuget.org) with the account/organization
     that owns (or will own) the `AVMTradeReporter.Models` package.
   - Click your username -> **Trusted Publishing** -> **Add a policy**.
   - Fill in:
     - **Repository Owner:** `scholtz`
     - **Repository:** `AVMTradeReporter`
     - **Workflow File:** `publish-nuget.yml` (file name only, not the
       `.github/workflows/` path)
     - **Environment:** leave empty (the workflow doesn't use a GitHub Actions
       `environment:`)
   - Choose the policy owner (your user, or the org, if the package should be
     owned by an org). This must match the account you'll publish under.

2. **(Optional but recommended) Add a `NUGET_USER` secret**
   - The `NuGet/login` action needs your nuget.org **profile name** (not email)
     passed as `user`. The workflow reads it from the `NUGET_USER` repository
     secret so it isn't hardcoded in the workflow file.
   - In the GitHub repository, go to **Settings -> Secrets and variables ->
     Actions -> New repository secret**.
   - Name: `NUGET_USER`
   - Value: your nuget.org username (profile name shown at
     `nuget.org/profiles/<username>`).

3. **First publish activates the policy**
   - If this is a public repo, the policy is active immediately. For a private
     repo, a newly created policy is only *temporarily* active for 7 days —
     the first successful publish supplies nuget.org with the GitHub repo/owner
     IDs needed to lock the policy permanently. If 7 days pass with no publish,
     just restart the 7-day window on nuget.org.
   - No repository secret holds a long-lived credential; there is nothing to
     rotate or revoke beyond the policy itself.

No `NUGET_API_KEY` secret is used or required by this workflow.

## Consuming the package

```
dotnet add package AVMTradeReporter.Models
```

or reference a specific published build:

```
dotnet add package AVMTradeReporter.Models --version 1.0.1.2026071212
```
