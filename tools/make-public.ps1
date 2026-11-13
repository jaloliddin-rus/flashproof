# Makes the public snapshot of FlashProof's source (decided with the user on 14 Nov 2026). This repository stays
# private and keeps everything; the branch `public` gets one commit per publish, holding only the files in $Public,
# taken from the committed HEAD (uncommitted changes are never included). It touches neither the working tree nor the
# current branch, and pushes nothing: check the snapshot, then push it yourself with `git push <remote> public:main`.
#   pwsh -File tools/make-public.ps1 -List
#   pwsh -File tools/make-public.ps1 -AuthorName "<name>" -AuthorEmail "<id>+<user>@users.noreply.github.com" -Message "<message>"
[CmdletBinding(DefaultParameterSetName = 'List')]
param(
    [Parameter(ParameterSetName = 'List', Mandatory)] [switch] $List,
    [Parameter(ParameterSetName = 'Commit', Mandatory)] [string] $AuthorName,
    [Parameter(ParameterSetName = 'Commit', Mandatory)] [string] $AuthorEmail,
    [Parameter(ParameterSetName = 'Commit', Mandatory)] [string] $Message
)
$ErrorActionPreference = 'Stop'

# The public files and folders. Everything else stays private, including files added later, until it's listed here.
$Public = @(
    '.github', '.gitignore', 'Directory.Build.props', 'FlashProof.slnx', 'global.json', 'LICENSE', 'README.md',
    'THIRD-PARTY-NOTICES.txt', 'docs', 'src', 'tests', 'tools', 'publish.ps1', 'v1-design.md', 'research-comparison.md',
    'Test-UsbDrive.ps1'
)
$root = Split-Path $PSScriptRoot -Parent

function GitOut {
    $out = & git -C $root @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed." }
    $out
}

$index = Join-Path ([IO.Path]::GetTempPath()) "flashproof-public-$PID.index"
try {
    $source = GitOut rev-parse HEAD
    $dirty = GitOut status --porcelain -- @Public
    if ($dirty) { Write-Warning "Uncommitted changes in public files are left out (the snapshot is HEAD):`n$($dirty -join "`n")" }

    # A throwaway index: HEAD's files minus the private ones.
    $env:GIT_INDEX_FILE = $index
    GitOut read-tree $source | Out-Null
    GitOut rm --cached -r -q --ignore-unmatch -- . @($Public | ForEach-Object { ":(exclude)$_" }) | Out-Null
    $files = @(GitOut -c core.quotePath=false ls-files)
    $private = @(GitOut -c core.quotePath=false ls-tree -r --name-only $source | Where-Object { $_ -notin $files })

    # Nothing personal, and no pointers to private files, may go out. This script names them, so it isn't searched.
    # Personal: the git email, the Windows user and computer names, and this PC's network card (MAC) addresses, which
    # Windows puts at the end of some volume IDs (`\\?\Volume{…-11f1-…-<MAC>}`) that are easy to copy into tests.
    $macs = @(Get-NetAdapter -IncludeHidden | ForEach-Object { $_.MacAddress -replace '-', '' })
    $personal = @((GitOut config user.email), $env:USERNAME, $env:COMPUTERNAME) + $macs | Where-Object { $_ -and $_.Length -ge 4 }
    $scope = @('--', '.', ':(exclude)tools/make-public.ps1')
    $hits = @(& git -C $root grep --cached -I -n -i -F @($personal | ForEach-Object { '-e'; $_ }) @scope) +
            @(& git -C $root grep --cached -I -n -E -e 'HANDOFF|CLAUDE\.md|m4-plan|usability-check|\]\([^)]*(flash-health-research|Diagnostic)' @scope)
    if ($hits.Count -gt 0) { throw "Not published: these lines are personal or point to private files:`n$($hits -join "`n")" }

    Write-Host "Public: $($files.Count) files from $($source.Substring(0, 7)). Left out: $($private -join ', ')."
    if ($List) { return $files }
    $tree = GitOut write-tree
}
finally {
    Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    Remove-Item $index -ErrorAction SilentlyContinue
}

$parent = & git -C $root rev-parse -q --verify refs/heads/public
if ($parent -and (GitOut rev-parse "$parent^{tree}") -eq $tree) {
    Write-Host "The public branch already holds exactly these files. Nothing to do."
    return
}
$names = 'GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL'
$values = $AuthorName, $AuthorEmail, $AuthorName, $AuthorEmail
try {
    for ($i = 0; $i -lt 4; $i++) { Set-Item "Env:$($names[$i])" $values[$i] }
    $commit = GitOut commit-tree $tree @(if ($parent) { '-p', $parent }) -m $Message
}
finally {
    foreach ($n in $names) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
}
GitOut update-ref refs/heads/public $commit $(if ($parent) { $parent } else { '0' * 40 }) | Out-Null
Write-Host "Made commit $($commit.Substring(0, 7)) on the branch 'public' ($($files.Count) files). Look at it with: git show --stat public"
Write-Host "Nothing was pushed. To publish: git push <remote> public:main"
