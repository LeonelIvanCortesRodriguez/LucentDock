param([switch]$Disconnect, [string]$Strata, [string]$Claude)
# Starts Claude Code's terminal interface with Strata's status line connected, so Strata's AI usage widget gets
# Claude's own 5-hour and weekly figures. Claude Code runs status lines only in its terminal interface: the
# Claude desktop app's Code tab and IDE panels don't, so use this terminal session for Claude work.
#
#   .\tools\claude-terminal.ps1               connect (if needed), then start Claude Code in this terminal
#   .\tools\claude-terminal.ps1 -Disconnect   remove Strata's status line (puts your settings file back)
#
# Nothing is downloaded: it uses a Claude Code that is already installed (on PATH, or an IDE extension's copy).
# Connecting changes one key, statusLine, in ~/.claude/settings.json, after a backup (see Settings > AI usage).
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..").Path

function Find-Strata {
    if ($Strata) { return (Resolve-Path $Strata).Path }
    # The installed Strata if it is 1.2 or later (it has AI usage), else this repository's build.
    $installed = Join-Path $env:LOCALAPPDATA "Programs\Strata\Strata.exe"
    if ((Test-Path $installed) -and [version](Get-Item $installed).VersionInfo.FileVersion -ge [version]"1.2.0.0") { return $installed }
    $dist = Join-Path $repo "dist\Strata.exe"
    if ((Test-Path $dist) -and [version](Get-Item $dist).VersionInfo.FileVersion -ge [version]"1.2.0.0") { return $dist }
    throw "No Strata 1.2 found (installed or dist\Strata.exe). Build it first, or pass -Strata <path>."
}

function Find-Claude {
    if ($Claude) { return (Resolve-Path $Claude).Path }
    $onPath = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    # IDE extensions ship the same CLI; take the newest copy.
    $roots = ".antigravity-ide", ".vscode", ".vscode-insiders", ".cursor", ".windsurf" | ForEach-Object { Join-Path $env:USERPROFILE "$_\extensions" }
    $found = foreach ($r in $roots) {
        if (Test-Path $r) { Get-ChildItem $r -Directory -Filter "anthropic.claude-code-*" | ForEach-Object { Join-Path $_.FullName "resources\native-binary\claude.exe" } | Where-Object { Test-Path $_ } }
    }
    $newest = $found | Sort-Object { [version](([regex]::Match($_, 'claude-code-(\d+\.\d+\.\d+)')).Groups[1].Value) } -Descending | Select-Object -First 1
    if ($newest) { return $newest }
    throw "Claude Code isn't installed for the terminal. Install it (see https://code.claude.com/docs), or pass -Claude <path to claude.exe>."
}

# Strata is a windowed app: Start-Process -Wait is what makes PowerShell wait for it and read its exit code.
function Invoke-Strata([string]$exe, [string]$flag) {
    $out = New-TemporaryFile
    try {
        $p = Start-Process $exe -ArgumentList $flag -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out.FullName
        Get-Content $out.FullName | Write-Host   # to the screen only: the function's one output is the exit code
        return $p.ExitCode
    } finally { Remove-Item $out.FullName -ErrorAction SilentlyContinue }
}

$strataExe = Find-Strata
if ($Disconnect) {
    $code = Invoke-Strata $strataExe "--disconnect-claude"
    if ($code -ne 0) { throw "Strata could not disconnect (exit $code)." }
    return
}

$code = Invoke-Strata $strataExe "--connect-claude"
switch ($code) {
    0 { }
    3 { throw "Claude Code already has a status line of your own; Strata won't replace it. Remove it first if you want AI usage." }
    default { throw "Strata could not update Claude Code's settings (exit $code)." }
}
$claudeExe = Find-Claude
Write-Host ""
Write-Host "Starting Claude Code ($claudeExe)."
Write-Host "First time only: pick a theme, sign in with your Claude account (Pro/Max), and trust this folder."
Write-Host "Then send any message. After Claude's first reply, the line under the prompt shows Strata's"
Write-Host "'5h ...% used  -  7d ...% used', and the dock's AI usage widget shows the same figures."
Write-Host ""
& $claudeExe
