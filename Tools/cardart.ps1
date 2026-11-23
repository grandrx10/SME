# Card sketches -> Assets/Resources/CardArt/<CardId>.png (with a .meta), using Tools/CardArt/CardArt.cs.
#   powershell -File Tools/cardart.ps1 -Pdf <file.pdf> -Cards Id1,Id2     (in the order the sketches appear in the PDF)
#   powershell -File Tools/cardart.ps1 -Png <file.png> -Cards Id
# An existing card's art is left alone unless -Force (its .meta is always kept).
param([string]$Pdf, [string]$Png, [string[]]$Cards, [switch]$Force, [string]$Out)
$ErrorActionPreference = "Stop"
$Cards = @($Cards | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Cards -or (-not $Pdf -and -not $Png)) { throw "Give -Pdf or -Png, and -Cards" }
$project = Split-Path $PSScriptRoot -Parent
if (-not $Out) { $Out = Join-Path $project "Assets\Resources\CardArt" }
$version = ((Get-Content (Join-Path $project "ProjectSettings\ProjectVersion.txt") | Select-String '^m_EditorVersion:').Line -split ':\s*')[1].Trim()
$u = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Data"
$dotnet = "$u\NetCoreRuntime\dotnet.exe"
$rt = Get-ChildItem "$u\NetCoreRuntime\shared\Microsoft.NETCore.App" | Select-Object -First 1
$tmp = Join-Path $env:TEMP "sme-cardart"
New-Item -ItemType Directory -Force $tmp, $Out | Out-Null
$src = Join-Path $PSScriptRoot "CardArt\CardArt.cs"
$dll = "$tmp\CardArt.dll"
if (-not (Test-Path $dll) -or (Get-Item $src).LastWriteTime -gt (Get-Item $dll).LastWriteTime) {
    $refs = Get-ChildItem $rt.FullName -Filter System*.dll | Where-Object { $_.Name -notmatch 'Native' } | ForEach-Object { "-r:`"$($_.FullName)`"" }
    Set-Content "$tmp\c.rsp" (@('-nologo', '-target:exe', "-out:`"$dll`"") + $refs + "`"$src`"") -Encoding utf8
    & $dotnet "$u\DotNetSdkRoslyn\csc.dll" "@$tmp\c.rsp" | Where-Object { $_ -match 'error' }
    Set-Content "$tmp\CardArt.runtimeconfig.json" "{`"runtimeOptions`":{`"framework`":{`"name`":`"Microsoft.NETCore.App`",`"version`":`"$($rt.Name)`"}}}" -Encoding ascii
}
$made = "$tmp\out"
if (Test-Path $made) { Remove-Item "$made\*.png" }
if ($Pdf) { & $dotnet $dll $made --pdf $Pdf @Cards } else { & $dotnet $dll $made --png $Png $Cards[0] }
$art = Join-Path $project "Assets\Resources\CardArt"
$like = Get-ChildItem $art -Filter *.png.meta | Select-Object -First 1
foreach ($c in $Cards) {
    if (-not (Test-Path "$made\$c.png")) { Write-Host "!! no sketch for $c"; continue }
    $to = Join-Path $Out "$c.png"
    if ((Test-Path $to) -and -not $Force) { Write-Host "$c already has art (-Force replaces it)"; continue }
    Copy-Item "$made\$c.png" $to -Force
    if (-not (Test-Path "$to.meta")) {
        $t = [IO.File]::ReadAllText($like.FullName)
        [IO.File]::WriteAllText("$to.meta", [regex]::Replace($t, 'guid: [0-9a-f]{32}', "guid: $([guid]::NewGuid().ToString('N'))"))
    }
    Write-Host "$c -> $to"
}
