# Scans what would be published for personal / machine-specific data.
# Usage:  powershell -NoProfile -File tools/check-private.ps1                 (staged files)
#         powershell -NoProfile -File tools/check-private.ps1 -All            (all tracked + untracked, not ignored)
#         powershell -NoProfile -File tools/check-private.ps1 -All -Commits   (also every commit message)
# Exit code 1 if anything is found. Local identifiers are read from the environment at run time, and an optional
# local deny-list (.private-words.txt, git-ignored, see tools/make-private-words.ps1) adds this machine's own names,
# so this file never contains anyone's personal values. (ASCII only: Windows PowerShell 5.1.)
param([switch]$All, [switch]$Commits)
$ErrorActionPreference = 'Stop'
# git prints UTF-8; without this, Windows PowerShell decodes non-ASCII names (e.g. the author) with the OEM codepage.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Set-Location (git rev-parse --show-toplevel)

$files = if ($All) { git ls-files --cached --others --exclude-standard } else { git diff --cached --name-only --diff-filter=ACMR }
$files = @($files | Where-Object { $_ -and (Test-Path $_) -and $_ -notmatch '(^|/)vendor/.*\.min\.js$' })

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check($name, $regex) { $checks.Add([pscustomobject]@{ Name = $name; Regex = $regex }) }

# Local identifiers (from the environment, never hard-coded)
foreach ($v in @($env:USERNAME, $env:COMPUTERNAME, (Split-Path $env:USERPROFILE -Leaf))) {
    if ($v -and $v.Length -ge 3) { Add-Check 'local identifier' ('(?i)' + [regex]::Escape($v)) }
}
$gitEmail = git config --global user.email 2>$null
if ($gitEmail -and $gitEmail -notmatch 'noreply') { Add-Check 'git email' ('(?i)' + [regex]::Escape($gitEmail)) }

# This machine's own names (folder names, exact installed versions); one term per line, '#' comments.
if (Test-Path '.private-words.txt') {
    foreach ($w in Get-Content '.private-words.txt' -Encoding UTF8) {
        $w = $w.Trim()
        if ($w.Length -ge 4 -and -not $w.StartsWith('#')) { Add-Check 'private word' ('(?i)(?<![\w.])' + [regex]::Escape($w) + '(?![\w])') }
    }
}

# Generic patterns
Add-Check 'email address'         '(?i)\b(?!git@)[a-z0-9._%+-]+@(?!users\.noreply\.github\.com|example\.(com|org)|builtin\b)[a-z0-9.-]+\.[a-z]{2,}\b'
Add-Check 'user profile path'     '(?i)[a-z]:\\{1,2}users\\{1,2}(?!(user|public|default)\b|\.\.\.|<)[^\\"''\s]+'
Add-Check 'real Anthropic id'     '\b(req|msg|toolu)_(?!fixture)[A-Za-z0-9]{16,}'
Add-Check 'account data'          '(?i)"(email|orgId|orgName|accountUuid|organizationUuid|planTier)"\s*:'
Add-Check 'secret-looking value'  '(?i)(sk-ant-[a-z0-9-]{10,}|\bsk-[a-z0-9_-]{20,}|ghp_[a-z0-9]{20,}|gho_[a-z0-9]{20,}|github_pat_[a-z0-9_]{20,}|\bnpm_[a-z0-9]{30,}|pypi-[a-z0-9_-]{30,}|xox[abprs]-[a-z0-9-]{10,}|AIza[0-9a-z_-]{30,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----)'
Add-Check 'secret assignment'     '(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\s*[:=]\s*["'']?[^\s"''<>{}$]{8,}'
Add-Check 'real scan report file' '(?i)disk-raporu-\d{4}-\d\d-\d\d\.json'

# The repo's own public GitHub address and owner handle (from the origin remote) and the commit author name
# (LICENSE copyright line) are public by design. Read at run time so no handle or name is written here.
$publicRepo = $null
$origin = git remote get-url origin 2>$null
$publicOwner = $null
if ($origin -match 'github\.com[:/](([^/]+)/[^/.]+)') { $publicRepo = 'github.com/' + $Matches[1]; $publicOwner = $Matches[2] }
$authorName = git config user.name 2>$null

$found = 0
function Test-Line([string]$where, [string]$line) {
    if ($publicRepo) { $line = [regex]::Replace($line, [regex]::Escape($publicRepo), '<repo>', 'IgnoreCase') }
    # The owner's GitHub handle is public (repo URL, winget id BerkayCaglar.Ferahla style); a local name inside it is not a leak.
    if ($publicOwner) { $line = [regex]::Replace($line, [regex]::Escape($publicOwner), '<owner>', 'IgnoreCase') }
    if ($authorName) { $line = [regex]::Replace($line, [regex]::Escape($authorName), '<author>', 'IgnoreCase, CultureInvariant') }
    foreach ($c in $checks) {
        $ms = [regex]::Matches($line, $c.Regex)
        if ($ms.Count -eq 0) { continue }
        $script:found++
        Write-Host ("{0}  [{1}]  {2}" -f $where, $c.Name, (($ms | ForEach-Object { $_.Value } | Select-Object -Unique) -join ', '))
    }
}

foreach ($f in $files) {
    Test-Line "$f (file name)" $f   # a file name can leak as much as its content
    $lines = Get-Content -LiteralPath $f -Encoding UTF8 -ErrorAction SilentlyContinue
    for ($n = 0; $n -lt $lines.Count; $n++) { Test-Line ("{0}:{1}" -f $f, ($n + 1)) ([string]$lines[$n]) }
}

# Fixtures: every UUID must be a fake one, and no long opaque blobs (API signatures, encrypted thinking).
foreach ($f in $files | Where-Object { $_ -like 'tests/fixtures/*' }) {
    $u = Select-String -LiteralPath $f -Pattern '\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b' -AllMatches |
         ForEach-Object { $_.Matches.Value } | Where-Object { $_ -notmatch '^00000000-0000-4000-8000-' } | Select-Object -Unique
    foreach ($x in $u) { $found++; Write-Host "${f}:  [real-looking uuid in fixture]  $x" }
    $b = Select-String -LiteralPath $f -Pattern '[A-Za-z0-9+/]{120,}={0,2}' -AllMatches | Select-Object -First 1
    if ($b) { $found++; Write-Host "${f}:$($b.LineNumber)  [long opaque value in fixture (signature?)]  $($b.Matches[0].Value.Substring(0, 24))..." }
}

if ($Commits) {
    # The current branch's history (what a push publishes). Author/committer lines are checked on their own:
    # the name must be the configured author and the email a GitHub noreply address; message lines get every check.
    $sep = '--8<--commit--'
    $log = (git log HEAD --format="%H%n%an%n%ae%n%cn%n%ce%n%B$sep") -join "`n"
    foreach ($entry in $log -split [regex]::Escape($sep)) {
        $entry = $entry.Trim()
        if (-not $entry) { continue }
        $parts = $entry -split "`n"
        $hash = $parts[0].Substring(0, [Math]::Min(10, $parts[0].Length))
        foreach ($i in 1, 3) {
            if ($parts[$i] -ne $authorName) { $found++; Write-Host "commit $hash  [unexpected author/committer name]  $($parts[$i])" }
            if ($parts[$i + 1] -notmatch '@users\.noreply\.github\.com$') { $found++; Write-Host "commit $hash  [non-noreply email]  $($parts[$i + 1])" }
        }
        foreach ($l in ($parts | Select-Object -Skip 5)) { Test-Line "commit $hash" $l }
    }
}

if ($found) { Write-Host "check-private: $found finding(s). Fix before committing."; exit 1 }
Write-Host ("check-private: clean ({0} files{1})" -f $files.Count, $(if ($Commits) { ', commit messages' } else { '' }))
# Explicit: otherwise pwsh returns the last native command's exit code (e.g. `git config` with no value set).
exit 0
