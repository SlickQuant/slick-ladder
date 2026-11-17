# Release Process

This document describes how to create a new release of SlickLadder.

## Prerequisites

Before creating a release, ensure you have:

1. **NPM Token**: Set up `NPM_TOKEN` as a GitHub repository secret
   - Go to [npmjs.com](https://www.npmjs.com/) → Account Settings → Access Tokens
   - Create a new "Automation" token
   - Add it to GitHub: Repository Settings → Secrets → Actions → New secret
   - Name: `NPM_TOKEN`

2. **Updated CHANGELOG.md**: Document all changes in the `[Unreleased]` section

## Release Steps

### 1. Update Version and Changelog

```bash
# Update version in package.json files
npm version <major|minor|patch> --workspace=src/web --no-git-tag-version

# For example:
npm version patch --workspace=src/web --no-git-tag-version  # 0.1.1 → 0.1.2
npm version minor --workspace=src/web --no-git-tag-version  # 0.1.1 → 0.2.0
npm version major --workspace=src/web --no-git-tag-version  # 0.1.1 → 1.0.0
```

### 2. Update CHANGELOG.md

Move items from `[Unreleased]` to a new version section:

```markdown
## [Unreleased]

## [0.1.2] - 2026-01-17

### Added
- New feature X
- New feature Y

### Fixed
- Bug fix Z
```

### 3. Commit and Tag

```bash
# Commit version changes
git add package.json src/web/package.json CHANGELOG.md
git commit -m "Release v0.1.2"

# Create and push tag
git tag v0.1.2
git push origin main
git push origin v0.1.2
```

### 4. Automated Release Process

Once the tag is pushed, GitHub Actions will automatically:

1. ✅ Build NPM package (`@slickquant/slick-ladder-x.y.z.tgz`)
2. ✅ Build desktop binaries for all platforms:
   - `SlickLadder-win-x64.zip` (Windows)
   - `SlickLadder-linux-x64.zip` (Linux)
   - `SlickLadder-osx-x64.zip` (macOS Intel)
   - `SlickLadder-osx-arm64.zip` (macOS Apple Silicon)
3. ✅ Create GitHub Release with:
   - Release notes extracted from CHANGELOG.md
   - All build artifacts attached
4. ✅ Publish NPM package to npmjs.com

### 5. Verify Release

After the workflow completes (~10-15 minutes):

1. **GitHub Release**: Check [Releases](https://github.com/SlickQuant/slick-ladder/releases)
   - Verify all 5 files are attached (1 NPM package + 4 desktop binaries)
   - Confirm release notes are correct

2. **NPM Package**: Check [npmjs.com](https://www.npmjs.com/package/@slickquant/slick-ladder)
   - Verify new version is published
   - Test installation: `npm install @slickquant/slick-ladder@latest`

3. **Desktop Binaries**: Download and test each platform binary
   - Extract and run the Avalonia Demo app
   - Verify it runs without errors

## Manual Release (if needed)

If the automated workflow fails, you can release manually:

### NPM Package

```bash
# Build and publish
npm run build:lib:full
npm run release
```

### Desktop Binaries

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

Then create ZIP files and upload to GitHub Release manually.

## Hotfix Release

For urgent fixes:

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

### NPM publish fails with 403
- Verify `NPM_TOKEN` secret is set correctly
- Check token has "Automation" permissions
- Ensure package name in `src/web/package.json` is available

### Desktop build fails
- Check .NET SDK version (requires 8.0+)
- Verify all project references are valid
- Check for platform-specific dependencies

### Release workflow not triggering
- Ensure tag follows pattern `v*.*.*` (e.g., `v0.1.2`)
- Check GitHub Actions is enabled in repository settings
- Verify workflow file has no syntax errors
