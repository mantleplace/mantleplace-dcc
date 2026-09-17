<#
    The shared half of every host's Check-<Host>Install.ps1: git facts in, one verdict line out.

    Why a slot has to say what it holds, and the verdict contract every host prints, live in
    README.md beside this file; the rule is HPS-50. This file is the one implementation of that
    contract, so a host script never composes a verdict sentence of its own.

    Windows PowerShell 5.1 compatible, deliberately: the Revit host script runs wherever the deploy
    script runs, and that script's own header explains why 5.1 is the floor. No ternaries, no
    null-coalescing, no pipeline chain operators. The Pester file beside this is pwsh-only and that
    is fine; it is the test, not the code.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Runs git with stderr discarded and returns stdout, leaving the exit code in $LASTEXITCODE.

.DESCRIPTION
    Windows PowerShell 5.1 turns a native command's stderr into an error record when the stream is
    redirected, and under $ErrorActionPreference = 'Stop' that record terminates the script -- so a
    plain `git rev-parse HEAD 2>$null` outside a repository is a crash, not a $null. The preference
    is scoped to this function, which is the one place the scripts talk to git. Call it as
    `Invoke-Git -C <path> rev-parse HEAD`; every token after the name goes to git.
#>
function Invoke-Git {
    # A SIMPLE function on purpose: a [Parameter()] attribute or [CmdletBinding()] would make it
    # advanced, and then git's own `-e`, `-c` and `-C` bind to PowerShell's common parameters
    # (`-e` is ambiguous between -ErrorAction and -ErrorVariable). $args takes anything.
    $ErrorActionPreference = 'Continue'
    $output = & git @args 2>$null
    return $output
}

<#
.SYNOPSIS
    What a checkout is right now: its commit, its branch (or $null when detached), whether it has
    uncommitted changes, and its top-level directory. Nothing when the path is not a repository.
#>
function Get-GitCheckoutState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [string]$DirtyScope = '.'
    )

    $sha = Invoke-Git -C $Path rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) { return $null }

    $branch = Invoke-Git -C $Path rev-parse --abbrev-ref HEAD
    if ($LASTEXITCODE -ne 0 -or $branch -eq 'HEAD') { $branch = $null }

    $status = @(Invoke-Git -C $Path status --porcelain --untracked-files=all -- $DirtyScope)
    $dirty = ($LASTEXITCODE -eq 0) -and ($status.Count -gt 0)

    $topLevel = Invoke-Git -C $Path rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0) { $topLevel = $null }
    if ($null -ne $topLevel) { $topLevel = $topLevel -replace '/', '\' }

    return [pscustomobject]@{
        Sha      = [string]$sha
        Branch   = $branch
        Dirty    = $dirty
        TopLevel = $topLevel
    }
}

function Get-ShortSha {
    param([string]$Sha)
    if ([string]::IsNullOrWhiteSpace($Sha)) { return 'unknown' }
    if ($Sha.Length -le 7) { return $Sha }
    return $Sha.Substring(0, 7)
}

function Get-CommitCountPhrase {
    param([int]$Count, [string]$HostPath)
    $noun = if ($Count -eq 1) { 'commit' } else { 'commits' }
    $under = if ([string]::IsNullOrWhiteSpace($HostPath)) { '' } else { " under $HostPath" }
    return "$Count newer $noun$under"
}

function New-Verdict {
    param([string]$Status, [int]$ExitCode, [string]$Line)
    return [pscustomobject]@{
        Status   = $Status
        ExitCode = $ExitCode
        Line     = $Line
    }
}

<#
.SYNOPSIS
    Turns the facts a host script gathered into one verdict line and an exit code.

.PARAMETER HostName
    The word the line opens with: Revit, Unreal.
.PARAMETER Configured
    False when the host script could not find what it checks (no consuming project configured).
.PARAMETER Installed
    False when the slot is empty (no add-in in the folder, no submodule checkout).
.PARAMETER HasStamp
    False when the slot has files but nothing says where they came from.
.PARAMETER FromSourceTree
    False when the stamp is a release install: nothing to compare against a tree.
.PARAMETER Sha
    The installed commit, full or short.
.PARAMETER ShaKnown
    Whether this repository has that commit at all (git cat-file -e).
.PARAMETER Branch
    The branch the install came from, or null for a detached head.
.PARAMETER Dirty
    Whether the tree was dirty when the install was made.
.PARAMETER OnMain
    Whether the installed commit is reachable from origin/main.
.PARAMETER BehindMain
    Commits in <sha>..origin/main touching the host's folder.
.PARAMETER BranchExists
    Whether origin/<branch> exists.
.PARAMETER BehindBranch
    Commits in <sha>..origin/<branch> touching the host's folder.
.PARAMETER BranchMerged
    Whether origin/<branch> is reachable from origin/main.
.PARAMETER HostPath
    The folder the counts were scoped to, for the sentence: revit/, unreal/.
.PARAMETER Remedy
    What to run when stale.
.PARAMETER InstalledAt
    When, as the stamp wrote it. Optional.
.PARAMETER SourceTree
    Where from. Optional.
#>
function Get-LocalInstallVerdict {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$HostName,
        [bool]$Configured = $true,
        [bool]$Installed = $true,
        [bool]$HasStamp = $true,
        [bool]$FromSourceTree = $true,
        [string]$Sha = $null,
        [bool]$ShaKnown = $true,
        [string]$Branch = $null,
        [bool]$Dirty = $false,
        [bool]$OnMain = $false,
        [int]$BehindMain = 0,
        [bool]$BranchExists = $false,
        [int]$BehindBranch = 0,
        [bool]$BranchMerged = $false,
        [string]$HostPath = '',
        [string]$Remedy = 'the host''s deploy script',
        [string]$InstalledAt = $null,
        [string]$SourceTree = $null
    )

    $installedText = if ([string]::IsNullOrWhiteSpace($InstalledAt)) { '' } else { " installed $InstalledAt" }

    if (-not $Configured) {
        return New-Verdict 'NotConfigured' 0 "${HostName}: not configured - nothing to check on this machine"
    }
    if (-not $Installed) {
        return New-Verdict 'NotInstalled' 1 "${HostName}: not installed"
    }
    if (-not $HasStamp) {
        return New-Verdict 'Unverified' 1 "${HostName}: unverified - no install stamp, so the files were copied by hand or from a release zip. Run $Remedy to install a build that says what it is."
    }
    if (-not $FromSourceTree) {
        return New-Verdict 'Unverified' 1 "${HostName}: unverified -$installedText from a release zip, not from a source tree. Run $Remedy to track main."
    }

    $short = Get-ShortSha $Sha
    if ([string]::IsNullOrWhiteSpace($Sha)) {
        return New-Verdict 'Unverified' 1 "${HostName}: unverified - the stamp names no commit, so the deploy ran without git. Run $Remedy from a checkout."
    }
    if (-not $ShaKnown) {
        return New-Verdict 'Unverified' 1 "${HostName}: unverified - commit $short is not in this repository. Fetch, or the install came from a tree this clone has never seen."
    }

    $where = if ([string]::IsNullOrWhiteSpace($SourceTree)) { '' } else { " from $SourceTree" }
    $onBranch = -not [string]::IsNullOrWhiteSpace($Branch)
    $label = if ($onBranch) { "$Branch@$short" } else { "a detached head at $short" }

    if ($Dirty) {
        return New-Verdict 'Unverified' 1 "${HostName}: unverified - $label was deployed from a tree with uncommitted changes$where. Commit or stash, then run $Remedy."
    }

    if ($onBranch -and $Branch -eq 'main') {
        if ($BehindMain -gt 0) {
            $count = Get-CommitCountPhrase $BehindMain $HostPath
            return New-Verdict 'Stale' 1 "${HostName}: stale - main@$short$installedText; origin/main has $count. Run $Remedy from main."
        }
        $when = if ($installedText -eq '') { '' } else { ",$installedText" }
        return New-Verdict 'Current' 0 "${HostName}: current - main@$short$when$where"
    }

    # A preview: a branch, or no branch at all.
    if ($onBranch -and $BranchExists -and $BranchMerged) {
        if ($BehindMain -gt 0) {
            $count = Get-CommitCountPhrase $BehindMain $HostPath
            return New-Verdict 'Stale' 1 "${HostName}: stale - preview $label has merged; origin/main has $count. Run $Remedy from main."
        }
        $scope = if ([string]::IsNullOrWhiteSpace($HostPath)) { '' } else { " under $HostPath" }
        return New-Verdict 'Stale' 1 "${HostName}: stale - preview $label has merged and nothing newer has landed$scope; the slot still says a branch. Run $Remedy from main so it says main."
    }
    if ($onBranch -and $BranchExists) {
        if ($BehindBranch -gt 0) {
            $count = Get-CommitCountPhrase $BehindBranch $HostPath
            return New-Verdict 'Stale' 1 "${HostName}: stale - preview $label; origin/$Branch has $count. Run $Remedy from that branch, or from main."
        }
        $when = if ($installedText -eq '') { '' } else { ",$installedText" }
        return New-Verdict 'Preview' 0 "${HostName}: preview - $label$when, current with origin/$Branch. Not main."
    }
    if ($onBranch) {
        # origin has no such branch. Either it was never pushed, or it merged and was deleted --
        # and this repository squash-merges, so a merged preview's commit is never on main. The
        # two cases cannot be told apart from here; what can be told is whether main moved.
        if ($BehindMain -gt 0) {
            $count = Get-CommitCountPhrase $BehindMain $HostPath
            return New-Verdict 'Stale' 1 "${HostName}: stale - preview $label, and origin has no such branch now (merged and deleted, or never pushed); origin/main has $count. Run $Remedy from main."
        }
        $when = if ($installedText -eq '') { '' } else { ",$installedText" }
        return New-Verdict 'Preview' 0 "${HostName}: preview - $label, which origin does not have$when. Not main."
    }
    if ($BehindMain -gt 0) {
        $count = Get-CommitCountPhrase $BehindMain $HostPath
        return New-Verdict 'Stale' 1 "${HostName}: stale - $label; origin/main has $count. Run $Remedy from main."
    }
    $when = if ($installedText -eq '') { '' } else { ",$installedText" }
    $notMain = if ($OnMain) { 'behind origin/main' } else { 'not on origin/main' }
    return New-Verdict 'Preview' 0 "${HostName}: preview - $label, $notMain$when. Not main."
}

<#
.SYNOPSIS
    The git facts behind a verdict, read from this repository: is the commit known, is it on
    origin/main, how far behind is it under the host's folder, and the same for its branch.

.DESCRIPTION
    Impure on purpose and shared on purpose. Every host asks the same four questions of the same
    repository; only where the commit and branch come from differs (a stamp file for Revit, the
    submodule checkout for Unreal). Counts are scoped to -HostPath so a docs-only merge never asks
    for a redeploy. Fetches first unless -NoFetch: the whole point is comparing against what
    origin has, in a tree other sessions move under you.

.PARAMETER RepoRoot
    A checkout of mantleplace-dcc whose origin is the repository. Worktrees share objects, so any
    of them answers for a commit made in any other.
#>
function Get-GitInstallFacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$RepoRoot,
        [Parameter(Mandatory = $true)] [string]$HostPath,
        [string]$Sha = $null,
        [string]$Branch = $null,
        [switch]$NoFetch
    )

    $facts = [pscustomobject]@{
        ShaKnown     = $false
        OnMain       = $false
        BehindMain   = 0
        BranchExists = $false
        BehindBranch = 0
        BranchMerged = $false
        FetchFailed  = $false
    }

    if (-not $NoFetch) {
        Invoke-Git -C $RepoRoot fetch origin --quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { $facts.FetchFailed = $true }
    }

    if ([string]::IsNullOrWhiteSpace($Sha)) { return $facts }

    Invoke-Git -C $RepoRoot cat-file -e "$Sha^{commit}" | Out-Null
    if ($LASTEXITCODE -ne 0) { return $facts }
    $facts.ShaKnown = $true

    Invoke-Git -C $RepoRoot merge-base --is-ancestor $Sha origin/main | Out-Null
    $facts.OnMain = ($LASTEXITCODE -eq 0)

    $behind = Invoke-Git -C $RepoRoot rev-list --count "$Sha..origin/main" -- $HostPath
    if ($LASTEXITCODE -eq 0) { $facts.BehindMain = [int]$behind }

    if (-not [string]::IsNullOrWhiteSpace($Branch) -and $Branch -ne 'main') {
        Invoke-Git -C $RepoRoot rev-parse --verify --quiet "origin/$Branch" | Out-Null
        $facts.BranchExists = ($LASTEXITCODE -eq 0)
        if ($facts.BranchExists) {
            $behindBranch = Invoke-Git -C $RepoRoot rev-list --count "$Sha..origin/$Branch" -- $HostPath
            if ($LASTEXITCODE -eq 0) { $facts.BehindBranch = [int]$behindBranch }
            Invoke-Git -C $RepoRoot merge-base --is-ancestor "origin/$Branch" origin/main | Out-Null
            $facts.BranchMerged = ($LASTEXITCODE -eq 0)
        }
    }

    return $facts
}

Export-ModuleMember -Function Invoke-Git, Get-GitCheckoutState, Get-LocalInstallVerdict, Get-GitInstallFacts
