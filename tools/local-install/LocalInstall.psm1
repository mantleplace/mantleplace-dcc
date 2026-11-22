Set-StrictMode -Version Latest

function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    $output = & git @args 2>$null
    return $output
}

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
