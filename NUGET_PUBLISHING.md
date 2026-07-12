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

## One-time setup: NuGet API key secret

The workflow pushes the package using an API key stored in the repository secret
`NUGET_API_KEY`. To set this up:

1. **Create a NuGet.org API key**
   - Sign in at [nuget.org](https://www.nuget.org) with the account/organization
     that owns (or will own) the `AVMTradeReporter.Models` package.
   - Go to **Account -> API Keys -> Create**.
   - Give it a name (e.g. `AVMTradeReporter-github-actions`).
   - Set **Glob Pattern** to `AVMTradeReporter.Models*` (or `AVMTradeReporter.Models` for
     an exact match) so the key can only push this package, not your whole account.
   - Select scope **Push new packages and package versions**.
   - Set an expiration date and copy the generated key — NuGet only shows it once.

2. **Add the key as a GitHub Actions secret**
   - In the GitHub repository, go to **Settings -> Secrets and variables -> Actions**.
   - Click **New repository secret**.
   - Name: `NUGET_API_KEY`
   - Value: paste the API key from step 1.
   - Click **Add secret**.

3. **First publish**
   - The very first push of a new package ID reserves the package name for the
     publishing account on NuGet.org. After that, only the same account (or a key
     scoped to the package) can publish new versions, so keep the API key secret
     safe and rotate it before it expires (repeat step 1-2 with a new key).

No other secrets are required for this workflow.

## Consuming the package

```
dotnet add package AVMTradeReporter.Models
```

or reference a specific published build:

```
dotnet add package AVMTradeReporter.Models --version 1.0.1.2026071212
```
