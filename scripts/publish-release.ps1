<#
.SYNOPSIS
    End-to-end release publishing script for Lambda Boss.

.DESCRIPTION
    Walks through every step of building, signing, and publishing a Lambda Boss release.
    Must be run from the main branch with a clean working directory.

    Steps:
      1. Prompt for new version, certificate path, and password
      2. Create release branch, bump version (props), push, and open PR
      3. Build in Release configuration
      4. Run tests
      5. Sign the packed XLL with code-signing certificate
      6. Bundle the signed XLL + README + unblock.cmd into LambdaBoss-<version>.zip
      7. Merge the version bump PR (only after build+sign succeed)
      8. Create git tag on the merge commit
      9. Create GitHub Release (draft) with the release zip

    Prompts for version, certificate path, and password. No secrets are stored.
    The version bump only lands on main after the entire build and sign process
    succeeds, so a failed release never leaves main in a bumped state.

.PARAMETER Version
    The version to release (e.g. "0.2.0"). Prompted if not provided.

.PARAMETER CertPath
    Path to the .pfx code-signing certificate. Prompted if not provided.

.PARAMETER CertPassword
    Password for the .pfx certificate. Prompted securely if not provided.

.PARAMETER SkipTests
    Skip running tests (use when you've already verified).

.PARAMETER SkipSign
    Skip code signing (for local testing only).

.PARAMETER DryRun
    Show what would happen without executing destructive steps (commit, tag, release).

.EXAMPLE
    .\scripts\publish-release.ps1
    .\scripts\publish-release.ps1 -Version "0.2.0" -CertPath "C:\certs\sectigo.pfx"
    .\scripts\publish-release.ps1 -DryRun -SkipSign
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$CertPath,
    [string]$CertPassword,
    [switch]$SkipTests,
    [switch]$SkipSign,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$BuildOutput = "$RepoRoot\addin\lambda-boss\bin\Release\net48\publish"
$SolutionPath = "$RepoRoot\addin\lambda-boss.slnx"
$ReleaseDir = "$RepoRoot\release"
$ReleaseOutputDir = "$ReleaseDir\output"
$BundleScript = "$RepoRoot\scripts\build-release-bundle.ps1"
$SignTool = "C:\Program Files (x86)\Microsoft SDKs\ClickOnce\SignTool\signtool.exe"
$TimestampServer = "http://timestamp.sectigo.com"

# --- Helpers ---

function Write-Step([int]$StepNumber, [string]$StepDescription) {
    Write-Host ""
    Write-Host "=== Step ${StepNumber}: ${StepDescription} ===" -ForegroundColor Cyan
}

function Write-Success([string]$Message) {
    Write-Host "  OK: $Message" -ForegroundColor Green
}

function Write-Skip([string]$Message) {
    Write-Host "  SKIP: $Message" -ForegroundColor Yellow
}

function Confirm-Continue([string]$Prompt) {
    $response = Read-Host "$Prompt (y/n)"
    if ($response -notin @("y", "Y", "yes")) {
        Write-Host "Aborted." -ForegroundColor Red
        exit 1
    }
}

# --- Step 0: Preflight checks ---

Write-Host ""
Write-Host "Lambda Boss Release Publisher" -ForegroundColor Magenta
Write-Host "=============================" -ForegroundColor Magenta

if (-not $SkipSign -and -not (Test-Path $SignTool)) {
    Write-Error "signtool.exe not found at: $SignTool`nInstall the ClickOnce Publishing Tools via Visual Studio Installer."
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) not found. Install from https://cli.github.com/"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK not found on PATH."
}
if (-not (Test-Path $BundleScript)) {
    Write-Error "Bundle script not found at: $BundleScript"
}

$currentBranch = git -C $RepoRoot rev-parse --abbrev-ref HEAD
if ($currentBranch -ne "main") {
    Write-Error "Must be on the main branch to publish a release. Current branch: $currentBranch"
}

$gitStatus = git -C $RepoRoot status --porcelain
if ($gitStatus) {
    Write-Host ""
    Write-Host "Uncommitted changes:" -ForegroundColor Red
    Write-Host $gitStatus
    Write-Error "Working directory must be clean before publishing. Commit or stash your changes."
}

git -C $RepoRoot pull --ff-only
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to pull latest main. Resolve any divergence first." }

# --- Step 1: Collect inputs ---

Write-Step 1 "Collect release inputs"

$propsFile = "$RepoRoot\Directory.Build.props"
[xml]$props = Get-Content $propsFile
$currentVersion = $props.Project.PropertyGroup.Version
if (-not $currentVersion) {
    Write-Error "Could not read <Version> from $propsFile"
}

Write-Host "  Current version: $currentVersion"

if (-not $Version) {
    $Version = Read-Host "New version (e.g. 0.2.0)"
}

if (-not ($Version -match '^\d+\.\d+\.\d+$')) {
    Write-Error "Invalid version format: $Version (expected X.Y.Z)"
}

if ($Version -eq $currentVersion) {
    Write-Host "  Version unchanged: $Version" -ForegroundColor Yellow
    Confirm-Continue "Release with the same version?"
}

$version = $Version
$packedXllPath = "$BuildOutput\lambda-boss64-packed.xll"
$zipName = "LambdaBoss-$version.zip"
$zipPath = "$ReleaseOutputDir\$zipName"
$tag = "v$version"
$releaseBranch = "release/v$version"

$existingTag = git -C $RepoRoot tag -l $tag
if ($existingTag) {
    Write-Host ""
    Write-Host "WARNING: Tag $tag already exists!" -ForegroundColor Yellow
    Confirm-Continue "This will overwrite the existing tag. Continue?"
}

if (-not $SkipSign) {
    if (-not $CertPath) {
        $CertPath = Read-Host "Path to .pfx certificate"
    }
    if (-not (Test-Path $CertPath)) {
        Write-Error "Certificate not found: $CertPath"
    }
    Write-Success "Certificate: $CertPath"

    if (-not $CertPassword) {
        $securePwd = Read-Host "Certificate password" -AsSecureString
        $CertPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd)
        )
    }
} else {
    Write-Skip "Code signing (--SkipSign)"
}

Write-Host ""
Write-Host "  Version: $currentVersion -> $version"
Write-Host "  Branch:  $releaseBranch"
Write-Host "  Zip:     $zipName"
Write-Host "  Tag:     $tag"

# --- Step 2: Create release branch and version bump PR ---

Write-Step 2 "Create version bump PR"

if (-not $DryRun) {
    git -C $RepoRoot checkout -b $releaseBranch
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create branch $releaseBranch" }

    $propsContent = Get-Content $propsFile -Raw
    $propsContent = $propsContent -replace '<Version>.*?</Version>', "<Version>$version</Version>"
    Set-Content $propsFile $propsContent -NoNewline

    git -C $RepoRoot add $propsFile
    git -C $RepoRoot commit -m "Bump version to $version"
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to commit version bump." }

    git -C $RepoRoot push -u origin $releaseBranch
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to push release branch." }

    gh pr create --repo TagloGit/lambda-boss `
        --base main `
        --head $releaseBranch `
        --title "Release v$version" `
        --body "Version bump for v$version release. Will be merged automatically by the publish script after build and signing succeed."
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create PR." }
    Write-Success "Release branch and PR created"
} else {
    $propsContent = Get-Content $propsFile -Raw
    $propsContent = $propsContent -replace '<Version>.*?</Version>', "<Version>$version</Version>"
    Set-Content $propsFile $propsContent -NoNewline
    Write-Skip "Release branch and PR (--DryRun)"
}

# --- Step 3: Build ---

Write-Step 3 "Build in Release configuration"

dotnet build $SolutionPath -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed." }
Write-Success "Build succeeded"

if (-not (Test-Path $packedXllPath)) {
    Write-Error "Expected packed XLL not found at: $packedXllPath"
}

# --- Step 4: Tests ---

if (-not $SkipTests) {
    Write-Step 4 "Run tests"
    dotnet test $SolutionPath -c Release --no-build
    if ($LASTEXITCODE -ne 0) { Write-Error "Tests failed." }
    Write-Success "All tests passed"
} else {
    Write-Step 4 "Run tests"
    Write-Skip "Tests (--SkipTests)"
}

# --- Step 5: Sign the packed XLL ---

if (-not $SkipSign) {
    Write-Step 5 "Sign the packed XLL"

    & $SignTool sign /f $CertPath /p $CertPassword /fd sha256 /tr $TimestampServer /td sha256 $packedXllPath
    if ($LASTEXITCODE -ne 0) { Write-Error "XLL signing failed." }

    & $SignTool verify /pa $packedXllPath
    if ($LASTEXITCODE -ne 0) { Write-Error "XLL signature verification failed." }
    Write-Success "Packed XLL signed and verified"
} else {
    Write-Step 5 "Sign the packed XLL"
    Write-Skip "XLL signing (--SkipSign)"
}

# --- Step 6: Build the release zip ---

Write-Step 6 "Build release bundle"

& $BundleScript -Version $version -SignedXllPath $packedXllPath
if ($LASTEXITCODE -ne 0) { Write-Error "Release bundle build failed." }

if (-not (Test-Path $zipPath)) {
    Write-Error "Expected release zip not found at: $zipPath"
}
Write-Success "Release zip built: $zipPath"

# --- Summary before publish ---

Write-Host ""
Write-Host "=============================" -ForegroundColor Magenta
Write-Host "Ready to publish!" -ForegroundColor Magenta
Write-Host "=============================" -ForegroundColor Magenta
Write-Host ""
Write-Host "  Version: $version"
Write-Host "  Tag:     $tag"
Write-Host "  Zip:     $zipPath"
$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host ("  Size:    {0:N1} MB" -f $zipSize)
Write-Host ""

if ($DryRun) {
    Write-Host "DRY RUN -- skipping PR merge, tag, and GitHub Release." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Would run:" -ForegroundColor Yellow
    Write-Host "  gh pr merge $releaseBranch --squash --delete-branch"
    Write-Host "  git tag $tag"
    Write-Host "  git push origin $tag"
    Write-Host "  gh release create $tag --title 'Lambda Boss $version' ..."
    git -C $RepoRoot checkout -- $propsFile
    exit 0
}

Confirm-Continue "Merge version bump PR, tag, and create GitHub Release?"

# --- Step 7: Merge version bump PR ---

Write-Step 7 "Merge version bump PR"

gh pr merge $releaseBranch `
    --repo TagloGit/lambda-boss `
    --squash `
    --admin `
    --delete-branch
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to merge PR. Merge manually and re-run." }
Write-Success "Version bump PR merged"

git -C $RepoRoot checkout main
git -C $RepoRoot pull --ff-only
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to pull merged main." }
Write-Success "On main with version bump"

# --- Step 8: Tag the merge commit ---

Write-Step 8 "Create git tag"

$existingTag = git -C $RepoRoot tag -l $tag
if ($existingTag) {
    git -C $RepoRoot tag -d $tag
}

git -C $RepoRoot tag $tag
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create tag." }
Write-Success "Tag $tag created locally"

git -C $RepoRoot push origin $tag
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to push tag." }
Write-Success "Tag pushed to origin"

# --- Step 9: Create GitHub Release ---

Write-Step 9 "Create GitHub Release"

$releaseNotesFile = [System.IO.Path]::GetTempFileName()
try {
    $notes = @(
        "## Lambda Boss $version",
        "",
        "### What's New",
        "<!-- TODO: Replace these placeholder bullets with actual release highlights -->",
        "- <TODO: describe key change 1>",
        "- <TODO: describe key change 2>",
        "- <TODO: describe key change 3>",
        "",
        "### Requirements",
        "- 64-bit Excel (Microsoft 365 or Excel 2019+)",
        "- Windows 10 / 11",
        "- No .NET runtime install required -- the add-in ships against .NET Framework 4.8, which is part of Windows.",
        "",
        "### Installation",
        "Download ``$zipName``, extract, and follow the load instructions in the bundled README.txt.",
        "Three load paths are documented (AddIns folder + Add-Ins dialog, any folder + Add-Ins dialog, or double-click)."
    )
    $notes -join "`n" | Set-Content $releaseNotesFile -NoNewline

    gh release create $tag `
        --repo TagloGit/lambda-boss `
        --title "Lambda Boss $version" `
        --notes-file $releaseNotesFile `
        --draft `
        $zipPath
} finally {
    Remove-Item $releaseNotesFile -ErrorAction SilentlyContinue
}

if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create GitHub Release." }

Write-Host ""
Write-Host "=============================" -ForegroundColor Green
Write-Host "Release created as DRAFT" -ForegroundColor Green
Write-Host "=============================" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Edit the release notes on GitHub"
Write-Host "  2. Download the zip from the release, test the XLL on a clean VM"
Write-Host "  3. Publish the release when ready"
Write-Host ""
