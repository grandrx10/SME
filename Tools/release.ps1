# Publish a new version of the game to GitHub Releases; players' launchers pick it up on their next start.
#   powershell -File Tools/release.ps1                 build, then publish as the next version
#   powershell -File Tools/release.ps1 -DryRun         build and package only (nothing uploaded)
#   powershell -File Tools/release.ps1 -SkipBuild      re-package the last build
#   -Notes "text"                                      release notes
# The build runs in batch mode on a private copy of the project (%LOCALAPPDATA%\SME-release\project), so the
# editor can stay open. The first build imports everything (slow); later ones reuse that copy's Library.
# It builds what's on disk, committed or not. Uses the GitHub login git already has (Git Credential Manager).
param([switch]$DryRun, [switch]$SkipBuild, [string]$Notes = "")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Project = Split-Path $PSScriptRoot -Parent
$Repo = "grandrx10/SME"
$Git = "C:\Program Files\Git\cmd\git.exe"
$Work = Join-Path $env:LOCALAPPDATA "SME-release"
$Copy = "$Work\project"
$Build = "$Work\build"
$Dist = "$Work\dist"
New-Item -ItemType Directory -Force $Work, $Dist | Out-Null

$unityVersion = ((Get-Content "$Project\ProjectSettings\ProjectVersion.txt" | Select-String '^m_EditorVersion:').Line -split ':\s*')[1].Trim()
$UnityData = "C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Data"
if (-not (Test-Path $UnityData)) { throw "Unity $unityVersion isn't installed" }

function Step($m) { Write-Host "== $m" -ForegroundColor Cyan }

# ---------- GitHub ----------
function Get-Token {
    $psi = New-Object Diagnostics.ProcessStartInfo $Git, "credential fill"
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
    $p = [Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write("protocol=https`nhost=github.com`n`n"); $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEnd(); $p.WaitForExit()
    $m = [regex]::Match($out, '(?m)^password=(.+)$')
    if (-not $m.Success) { throw "No GitHub login stored for git. Push once (git asks you to sign in), then retry." }
    return $m.Groups[1].Value.Trim()
}
$Headers = $null
function Api($method, $path, $body) {
    $req = @{ Method = $method; Uri = "https://api.github.com/repos/$Repo$path"; Headers = $Headers }
    if ($body) { $req.Body = ($body | ConvertTo-Json); $req.ContentType = "application/json" }
    return Invoke-RestMethod @req
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $DryRun) {
    $Headers = @{ Authorization = "Bearer $(Get-Token)"; "User-Agent" = "sme-release"; Accept = "application/vnd.github+json" }
}

# ---------- next version: one more than the highest vN tag ----------
$version = 1
if ($Headers) {
    $tags = @(Api GET "/releases?per_page=100" | ForEach-Object { $_.tag_name })
    foreach ($t in $tags) { if ($t -match '^v(\d+)$' -and [int]$Matches[1] -ge $version) { $version = [int]$Matches[1] + 1 } }
}
elseif (Test-Path "$Project\Assets\Resources\GameVersion.txt") {
    $cur = (Get-Content "$Project\Assets\Resources\GameVersion.txt" -Raw).Trim()
    if ($cur -match '^\d+$') { $version = [int]$cur + 1 }
}
Step "Version $version"

# ---------- build ----------
if (-not $SkipBuild) {
    # the editor reads this too, so it finds the lobbies of the version just released
    [IO.File]::WriteAllText("$Project\Assets\Resources\GameVersion.txt", "$version")

    Step "Syncing project copy"
    foreach ($d in "Assets", "Packages", "ProjectSettings") {
        robocopy "$Project\$d" "$Copy\$d" /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy $d failed ($LASTEXITCODE)" }
    }

    Step "Building (log: $Work\build.log)"
    $unity = "$UnityData\..\Unity.exe"
    $p = Start-Process $unity -Wait -PassThru -NoNewWindow -ArgumentList @(
        "-batchmode", "-quit", "-projectPath", "`"$Copy`"", "-buildTarget", "Win64",
        "-executeMethod", "SuddenMageEncounter.EditorTools.ReleaseBuild.Build",
        "-smeVersion", "$version", "-smeOut", "`"$Build`"", "-logFile", "`"$Work\build.log`"")
    if ($p.ExitCode -ne 0 -or -not (Test-Path "$Build\SuddenMageEncounter.exe")) {
        Get-Content "$Work\build.log" -Tail 40 | Select-String -Pattern 'error|exception|fail' | Select-Object -First 20 | Out-Host
        throw "Unity build failed (exit $($p.ExitCode)); see $Work\build.log"
    }
}
if (-not (Test-Path "$Build\SuddenMageEncounter.exe")) { throw "No build at $Build" }

# ---------- package ----------
Step "Packaging"
Get-ChildItem $Build -Directory -Filter "*DoNotShip*" | Remove-Item -Recurse -Force
$zip = "$Dist\SME-Windows.zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($Build, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("   SME-Windows.zip: {0:N0} MB" -f ((Get-Item $zip).Length / 1MB))

# launcher: Unity's Roslyn against the .NET Framework every Windows PC has; deterministic, so it only
# changes when its source does
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$src = "$Project\Launcher\SMELauncher.cs"
$launcherVersion = [regex]::Match((Get-Content $src -Raw), 'LauncherVersion\s*=\s*(\d+)').Groups[1].Value
$cscArgs = @("-nologo", "-target:winexe", "-optimize+", "-deterministic", "-nostdlib+", "-out:$Dist\SMELauncher.exe")
foreach ($r in "mscorlib", "System", "System.Core", "System.Drawing", "System.Windows.Forms", "System.IO.Compression", "System.IO.Compression.FileSystem") { $cscArgs += "-r:$fw\$r.dll" }
if (Test-Path "$Project\Launcher\icon.ico") { $cscArgs += "-win32icon:$Project\Launcher\icon.ico" }
$cscArgs += $src
& "$UnityData\NetCoreRuntime\dotnet.exe" "$UnityData\DotNetSdkRoslyn\csc.dll" @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Launcher didn't compile" }

[IO.File]::WriteAllText("$Dist\version.txt", "game=$version`nlauncher=$launcherVersion`n")
Write-Host "   game=$version launcher=$launcherVersion"

if ($DryRun) { Step "Dry run: files are in $Dist"; return }

# ---------- publish: draft first, so launchers never see a release without its files ----------
Step "Publishing v$version"
$body = "Download **SMELauncher.exe** and run it: it installs the game and keeps it up to date.`n`n$Notes"
$rel = Api POST "/releases" @{ tag_name = "v$version"; target_commitish = "main"; name = "Sudden Mage Encounter v$version"; body = $body; draft = $true }

$auth = "$Work\auth-header.txt"
[IO.File]::WriteAllText($auth, "Authorization: $($Headers.Authorization)")
try {
    foreach ($f in "version.txt", "SMELauncher.exe", "SME-Windows.zip") {
        Write-Host "   uploading $f"
        $code = & curl.exe -sS -o "$Work\upload.json" -w "%{http_code}" -X POST -H "@$auth" -H "User-Agent: sme-release" `
            -H "Content-Type: application/octet-stream" --data-binary "@$Dist\$f" `
            "https://uploads.github.com/repos/$Repo/releases/$($rel.id)/assets?name=$f"
        if ($code -ne "201") { throw "Upload of $f failed (HTTP $code): $(Get-Content "$Work\upload.json" -Raw)" }
    }
}
finally { Remove-Item $auth -ErrorAction SilentlyContinue }

$rel = Api PATCH "/releases/$($rel.id)" @{ draft = $false; make_latest = "true" }
Step "Done: $($rel.html_url)"
Write-Host "   Players: https://github.com/$Repo/releases/latest/download/SMELauncher.exe"
