# Offline compile check of Assets/Scripts with Unity's bundled Roslyn (runtime + editor assemblies).
param([string]$Project = "C:\Users\Richard\SuddenMageEncounter")
$ErrorActionPreference = "Stop"
$u = "C:\Program Files\Unity\Hub\Editor\6000.0.36f1\Editor\Data"
$dotnet = "$u\NetCoreRuntime\dotnet.exe"
$csc = "$u\DotNetSdkRoslyn\csc.dll"
$out = Join-Path $env:TEMP "sme-compile-check"
New-Item -ItemType Directory -Force $out | Out-Null

function Get-Refs($csproj) {
    $x = [xml](Get-Content (Join-Path $Project $csproj) -Raw)
    $refs = @()
    foreach ($h in $x.SelectNodes("//*[local-name()='HintPath']")) {
        $p = $h.InnerText
        if ($p -match 'ScriptAssemblies\\Assembly-CSharp\.dll$') { continue }
        $refs += $p
    }
    $defs = ($x.SelectNodes("//*[local-name()='DefineConstants']") | Select-Object -First 1).InnerText
    return @{ refs = $refs; defs = $defs }
}

function Invoke-Compile($name, $files, $refs, $defs) {
    $rsp = Join-Path $out "$name.rsp"
    $lines = @("-nologo", "-target:library", "-langversion:9.0", "-nostdlib-", "-nowarn:0169,0649,0414,CS8019", "-out:`"$out\$name.dll`"", "-define:$defs")
    foreach ($r in $refs) { $lines += "-r:`"$r`"" }
    foreach ($f in $files) { $lines += "`"$f`"" }
    Set-Content -Path $rsp -Value $lines -Encoding utf8
    $res = & $dotnet $csc "@$rsp" 2>&1
    $errs = $res | Where-Object { $_ -match ': error ' }
    $warns = $res | Where-Object { $_ -match ': warning ' }
    Write-Host "== $name : $($errs.Count) error(s), $($warns.Count) warning(s)"
    $errs | Select-Object -Unique | ForEach-Object { Write-Host $_.ToString().Replace($Project + '\', '') }
    $warns | Select-Object -Unique | Select-Object -First 30 | ForEach-Object { Write-Host $_.ToString().Replace($Project + '\', '') }
    return ($errs.Count -eq 0)
}

$scripts = Join-Path $Project "Assets\Scripts"
$all = Get-ChildItem $scripts -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$runtime = $all | Where-Object { $_ -notmatch '\\Editor\\' }
$editor = $all | Where-Object { $_ -match '\\Editor\\' }

$rt = Get-Refs "Assembly-CSharp.csproj"
$ok = Invoke-Compile "Assembly-CSharp" $runtime $rt.refs $rt.defs
if ($ok -and $editor.Count -gt 0) {
    $ed = Get-Refs "Assembly-CSharp-Editor.csproj"
    $refs = @($ed.refs | Where-Object { $_ -notmatch 'Assembly-CSharp\.dll$' }) + "$out\Assembly-CSharp.dll"
    Invoke-Compile "Assembly-CSharp-Editor" $editor $refs $ed.defs | Out-Host
}
