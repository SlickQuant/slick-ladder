# Releasing SlickLadder

## Before you start

You need three things in place:

1. An `NPM_TOKEN` repository secret on GitHub
   - On [npmjs.com](https://www.npmjs.com/), open Account Settings → Access Tokens and create an "Automation" token
   - In GitHub, open Repository Settings → Secrets → Actions → New secret and save the token as `NPM_TOKEN`
2. NuGet trusted publishing (one-time setup, no API key to rotate)
   - On [nuget.org](https://www.nuget.org/), open your username → Trusted Publishing and add a GitHub Actions policy: owner `SlickQuant`, repository `slick-ladder`, workflow file `release.yml`, environment empty. Its scope must allow new packages, since the first release creates the four `SlickLadder.*` IDs.
   - Add a `NUGET_USER` repository secret holding your nuget.org profile name (not your email)
3. Every change in the release listed under `[Unreleased]` in CHANGELOG.md

## Steps

### 1. Bump the version

```bash
# Update version in package.json files
npm version <major|minor|patch> --workspace=src/web --no-git-tag-version

# For example:
npm version patch --workspace=src/web --no-git-tag-version  # 0.1.1 → 0.1.2
npm version minor --workspace=src/web --no-git-tag-version  # 0.1.1 → 0.2.0
npm version major --workspace=src/web --no-git-tag-version  # 0.1.1 → 1.0.0
```

The NuGet packages read their version from `src/web/package.json` (see `Directory.Build.props`), so this bumps them too.

### 2. Update CHANGELOG.md

Move the `[Unreleased]` items into a new version section:

```markdown
## [Unreleased]

## [0.1.2] - 2026-01-17

### Added
- New feature X
- New feature Y

### Fixed
- Bug fix Z
```

### 3. Commit and tag

```bash
# Commit version changes
git add package.json src/web/package.json CHANGELOG.md
git commit -m "Release v0.1.2"

# Create and push tag
git tag v0.1.2
git push origin main
git push origin v0.1.2
```

### 4. What the workflow does

Pushing the tag starts a GitHub Actions workflow that:

1. Builds the npm package (`@slickquant/slick-ladder-x.y.z.tgz`)
2. Builds the desktop binaries:
   - `SlickLadder-win-x64.zip` (Windows)
   - `SlickLadder-linux-x64.zip` (Linux)
   - `SlickLadder-osx-x64.zip` (macOS Intel)
   - `SlickLadder-osx-arm64.zip` (macOS Apple Silicon)
3. Packs the NuGet packages `SlickLadder.Core`, `SlickLadder.Rendering`, `SlickLadder.Avalonia` and `SlickLadder.WPF`, and fails if their version doesn't match the tag
4. Creates a GitHub release with notes taken from CHANGELOG.md and every build artifact attached
5. Publishes the package to npmjs.com and the NuGet packages (with their `.snupkg` symbols) to nuget.org

### 5. Check the release

The workflow takes about 10 to 15 minutes. When it finishes:

1. On the [Releases](https://github.com/SlickQuant/slick-ladder/releases) page, make sure all 13 files are attached (the npm package, 4 desktop binaries, and 4 NuGet packages with their 4 symbol packages) and the notes are right.
2. On [npmjs.com](https://www.npmjs.com/package/@slickquant/slick-ladder), check that the new version is listed, then install it with `npm install @slickquant/slick-ladder@latest`.
3. On [nuget.org](https://www.nuget.org/packages?q=SlickLadder), check that the four packages show the new version. nuget.org validates and indexes a new version before it can be installed, which can take several minutes.
4. Download each desktop binary, extract it and run the Avalonia demo to make sure it starts without errors.

## Manual release

If the workflow fails, you can release by hand.

### npm package

```bash
# Build and publish
npm run build:lib:full
npm run release
```

### NuGet packages

Pack on Windows, since `SlickLadder.WPF` targets `net8.0-windows`. Pushing by hand needs an API key from nuget.org.

```bash
dotnet pack slick-ladder.sln -c Release -o nupkgs
dotnet nuget push "nupkgs/*.nupkg" --api-key <key> --source https://api.nuget.org/v3/index.json --skip-duplicate
```

### Desktop binaries

```bash
# Windows (x64)
dotnet publish examples/avalonia/SlickLadder.Avalonia.Demo.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/win-x64

# Linux (x64)
dotnet publish examples/avalonia/SlickLadder.Avalonia.Demo.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64

# macOS (Intel)
dotnet publish examples/avalonia/SlickLadder.Avalonia.Demo.csproj \
  -c Release -r osx-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/osx-x64

# macOS (Apple Silicon)
dotnet publish examples/avalonia/SlickLadder.Avalonia.Demo.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/osx-arm64
```

Then zip each output folder and upload the zips to the GitHub release.

## Hotfix release

For an urgent fix, branch from the last release tag:

```bash
# Create hotfix branch from tag
git checkout -b hotfix/v0.1.3 v0.1.2

# Make fixes and commit
git commit -m "Fix critical bug"

# Update version and changelog
npm version patch --workspace=src/web --no-git-tag-version

# Tag and push
git tag v0.1.3
git push origin hotfix/v0.1.3
git push origin v0.1.3

# Merge back to main
git checkout main
git merge hotfix/v0.1.3
git push origin main
```

## Troubleshooting

### npm publish fails with 403
- Check that the `NPM_TOKEN` secret is set and the token has "Automation" permissions
- Make sure the package name in `src/web/package.json` is available

### NuGet login or push fails
- `NUGET_USER` must be the nuget.org profile name, not the email
- The trusted publishing policy must match owner `SlickQuant`, repository `slick-ladder` and workflow file `release.yml` (the file name only), and its scope must cover the `SlickLadder.*` packages

### Desktop build fails
- You need the .NET 8.0 SDK or later
- Check the project references and any platform-specific dependencies

### Release workflow doesn't start
- The tag must match `v*.*.*`, for example `v0.1.2`
- GitHub Actions must be enabled in the repository settings
- Check the workflow file for syntax errors
