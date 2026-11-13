# The release build (design §9): a self-contained, single-file FlashProof exe for win-x64 and win-arm64, with nothing
# to install. Writes artifacts\release\: FlashProof-win-x64.exe, FlashProof-win-arm64.exe, LICENSE.txt,
# THIRD-PARTY-NOTICES.txt and SHA256SUMS.txt. Run from anywhere: pwsh -File publish.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$release = Join-Path $root 'artifacts\release'

foreach ($dir in $publish, $release) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory $release | Out-Null

foreach ($rid in 'win-x64', 'win-arm64') {
    $out = Join-Path $publish $rid
    dotnet publish (Join-Path $root 'src\FlashProof\FlashProof.csproj') -c Release -r $rid -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid." }
    Copy-Item (Join-Path $out 'FlashProof.exe') (Join-Path $release "FlashProof-$rid.exe")
}

Copy-Item (Join-Path $root 'LICENSE') (Join-Path $release 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $release

# "hash  name", the format sha256sum -c reads.
Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii

Get-ChildItem $release | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
