# The verdict a host's Check-<Host>Install.ps1 prints, tested over facts rather than over git.
#
# The host scripts gather facts (a stamp, a submodule checkout, a few rev-list counts) and hand
# them to Get-LocalInstallVerdict; this file pins what that function says for each shape of fact.
# The scripts themselves need a Revit add-ins folder or a consuming project's checkout and are
# proven by running them, the same way the deploy script is.
#
#   pwsh -NoProfile -Command "Invoke-Pester tools/local-install/LocalInstall.Tests.ps1"
#
# Pester 5+ syntax; the module under test stays Windows PowerShell 5.1 compatible and is loaded
# into 5.1 by the host scripts, so a construct that only pwsh accepts belongs here, never there.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'LocalInstall.psm1') -Force
}

Describe 'Get-LocalInstallVerdict' {

    Context 'the slot is empty or unreachable' {
        It 'says not configured, quietly, with exit 0' {
            $v = Get-LocalInstallVerdict -HostName Unreal -Configured $false
            $v.Status | Should -Be 'NotConfigured'
            $v.ExitCode | Should -Be 0
            $v.Line | Should -Match '^Unreal: not configured'
        }

        It 'says not installed, with a nonzero exit' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $false
            $v.Status | Should -Be 'NotInstalled'
            $v.ExitCode | Should -Be 1
            $v.Line | Should -Match '^Revit: not installed'
        }

        It 'treats a missing stamp as unverified, and names the deploy script' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $false
            $v.Status | Should -Be 'Unverified'
            $v.ExitCode | Should -Be 1
            $v.Line | Should -Match 'no install stamp'
        }

        It 'treats a release-zip stamp as unverified: nothing to compare against a tree' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -FromSourceTree $false -InstalledAt '2026-09-17T10:12:00-05:00' -Remedy 'x'
            $v.Status | Should -Be 'Unverified'
            $v.Line | Should -Be 'Revit: unverified - installed 2026-09-17T10:12:00-05:00 from a release zip, not from a source tree. Run x to track main.'
        }

        It 'treats a source-tree stamp with no commit as a deploy without git, not as a release' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true -Sha '' -Remedy 'x'
            $v.Status | Should -Be 'Unverified'
            $v.Line | Should -Match 'names no commit, so the deploy ran without git'
        }

        It 'treats a commit this repository does not have as unverified' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $false
            $v.Status | Should -Be 'Unverified'
            $v.Line | Should -Match 'abc1234 is not in this repository'
        }
    }

    Context 'an install from main' {
        It 'is current when nothing touching the host has landed since' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'e90b8502eb6b' -ShaKnown $true -Branch 'main' -Dirty $false `
                -OnMain $true -BehindMain 0 -InstalledAt '2026-09-17T10:12:00-05:00' `
                -SourceTree 'D:\GHW\mantleplace-dcc\main'
            $v.Status | Should -Be 'Current'
            $v.ExitCode | Should -Be 0
            $v.Line | Should -Be 'Revit: current - main@e90b850, installed 2026-09-17T10:12:00-05:00 from D:\GHW\mantleplace-dcc\main'
        }

        It 'is stale by the count of host commits, and says what to run' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha '7079c8b59f04' -ShaKnown $true -Branch 'main' -Dirty $false `
                -OnMain $true -BehindMain 12 -InstalledAt '2026-09-15T22:39:00-05:00' `
                -HostPath 'revit/' -Remedy 'revit/tools/Deploy-MantlePlaceRevit.ps1'
            $v.Status | Should -Be 'Stale'
            $v.ExitCode | Should -Be 1
            $v.Line | Should -Be 'Revit: stale - main@7079c8b installed 2026-09-15T22:39:00-05:00; origin/main has 12 newer commits under revit/. Run revit/tools/Deploy-MantlePlaceRevit.ps1 from main.'
        }

        It 'counts one commit in the singular' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha '7079c8b59f04' -ShaKnown $true -Branch 'main' -Dirty $false `
                -OnMain $true -BehindMain 1 -HostPath 'revit/' -Remedy 'x'
            $v.Line | Should -Match 'has 1 newer commit under revit/'
        }

        It 'is unverified when the stamp says the tree was dirty, whatever the counts say' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'e90b8502eb6b' -ShaKnown $true -Branch 'main' -Dirty $true `
                -OnMain $true -BehindMain 0 -SourceTree 'D:\GHW\mantleplace-dcc\main'
            $v.Status | Should -Be 'Unverified'
            $v.ExitCode | Should -Be 1
            $v.Line | Should -Match '^Revit: unverified - main@e90b850 was deployed from a tree with uncommitted changes'
        }
    }

    Context 'a preview from a branch' {
        It 'is a preview when current with its branch tip, exit 0' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch 'feat/ribbon' -Dirty $false `
                -OnMain $false -BehindMain 3 -BranchExists $true -BehindBranch 0 -BranchMerged $false `
                -InstalledAt '2026-09-17T10:12:00-05:00'
            $v.Status | Should -Be 'Preview'
            $v.ExitCode | Should -Be 0
            $v.Line | Should -Be 'Revit: preview - feat/ribbon@abc1234, installed 2026-09-17T10:12:00-05:00, current with origin/feat/ribbon. Not main.'
        }

        It 'is stale against its own branch when the tip moved' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch 'feat/ribbon' -Dirty $false `
                -OnMain $false -BehindMain 3 -BranchExists $true -BehindBranch 2 -BranchMerged $false `
                -HostPath 'revit/'
            $v.Status | Should -Be 'Stale'
            $v.ExitCode | Should -Be 1
            $v.Line | Should -Match 'preview feat/ribbon@abc1234; origin/feat/ribbon has 2 newer commits under revit/'
        }

        It 'is stale against main once the branch has merged, and says to return to main' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch 'feat/ribbon' -Dirty $false `
                -OnMain $true -BehindMain 1 -BranchExists $true -BranchMerged $true `
                -HostPath 'revit/' -Remedy 'revit/tools/Deploy-MantlePlaceRevit.ps1'
            $v.Status | Should -Be 'Stale'
            $v.Line | Should -Be 'Revit: stale - preview feat/ribbon@abc1234 has merged; origin/main has 1 newer commit under revit/. Run revit/tools/Deploy-MantlePlaceRevit.ps1 from main.'
        }

        It 'is stale when the branch is gone from origin and main moved under the host: the squash-merge case' {
            # A squash merge puts a NEW commit on main and deletes the branch, so the installed
            # commit is never on main and origin/<branch> no longer resolves. That is the normal
            # end of every pull request here, and it must not read as a healthy preview forever.
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch 'feat/ribbon' -Dirty $false `
                -OnMain $false -BehindMain 3 -BranchExists $false -BranchMerged $false `
                -HostPath 'revit/' -Remedy 'revit/tools/Deploy-MantlePlaceRevit.ps1'
            $v.Status | Should -Be 'Stale'
            $v.ExitCode | Should -Be 1
            $v.Line | Should -Be 'Revit: stale - preview feat/ribbon@abc1234, and origin has no such branch now (merged and deleted, or never pushed); origin/main has 3 newer commits under revit/. Run revit/tools/Deploy-MantlePlaceRevit.ps1 from main.'
        }

        It 'is a preview of an unpushed branch when origin has no such branch and main has not moved' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch 'feat/ribbon' -Dirty $false `
                -OnMain $false -BehindMain 0 -BranchExists $false -BranchMerged $false
            $v.Status | Should -Be 'Preview'
            $v.ExitCode | Should -Be 0
            $v.Line | Should -Match 'feat/ribbon@abc1234, which origin does not have'
        }

        It 'says a merged preview whose content is already main''s should still be redeployed, without claiming zero is newer' {
            $v = Get-LocalInstallVerdict -HostName Revit -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch 'feat/ribbon' -Dirty $false `
                -OnMain $true -BehindMain 0 -BranchExists $true -BranchMerged $true `
                -HostPath 'revit/' -Remedy 'revit/tools/Deploy-MantlePlaceRevit.ps1'
            $v.Status | Should -Be 'Stale'
            $v.Line | Should -Be 'Revit: stale - preview feat/ribbon@abc1234 has merged and nothing newer has landed under revit/; the slot still says a branch. Run revit/tools/Deploy-MantlePlaceRevit.ps1 from main so it says main.'
        }

        It 'is a preview from a detached head when there is no branch and main has not moved' {
            $v = Get-LocalInstallVerdict -HostName Unreal -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch $null -Dirty $false `
                -OnMain $false -BehindMain 0
            $v.Status | Should -Be 'Preview'
            $v.Line | Should -Match '^Unreal: preview - a detached head at abc1234, not on origin/main'
        }

        It 'is stale from a detached head once main has moved under the host' {
            # The consuming project's normal state is a detached checkout at its pin; when that pin
            # is behind, the editor compiles old code, and the line has to say so.
            $v = Get-LocalInstallVerdict -HostName Unreal -Installed $true -HasStamp $true `
                -Sha 'abc1234def' -ShaKnown $true -Branch $null -Dirty $false `
                -OnMain $false -BehindMain 3 -HostPath 'unreal/' -Remedy 'unreal/tools/Refresh-UnrealInstall.ps1'
            $v.Status | Should -Be 'Stale'
            $v.Line | Should -Be 'Unreal: stale - a detached head at abc1234; origin/main has 3 newer commits under unreal/. Run unreal/tools/Refresh-UnrealInstall.ps1 from main.'
        }
    }
}
