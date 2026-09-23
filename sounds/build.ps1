# Builds the cs2-zombie-mode sound addon: sounds\files\*.wav|mp3 -> .vsnd_c, soundevents -> .vsndevts_c.
# The result in game\csgo_addons\<Addon> is what you publish to the Workshop; MultiAddonManager then sends it
# to players when they join (see sounds\README.md).
#
#   python sounds\gen_soundevents.py
#   powershell -File sounds\build.ps1            # finds CS2 through the Steam libraries
#   powershell -File sounds\build.ps1 -Cs2 "D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"
#
# Needs the Counter-Strike 2 Workshop Tools. They are not in the Steam tools list; install them from inside the
# game: Settings -> search "Install Counter-Strike Workshop Tools" -> Yes, quit, let Steam download them.

param(
    [string]$Cs2 = "",
    [string]$Addon = "cs2_zombie_mode_sounds"
)

$ErrorActionPreference = "Stop"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Cs2) {
    $roots = @("C:\Program Files (x86)\Steam")
    $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($line in (Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches)) {
            foreach ($m in $line.Matches) { $roots += $m.Groups[1].Value.Replace('\\', '\') }
        }
    }
    foreach ($r in $roots) {
        $candidate = Join-Path $r "steamapps\common\Counter-Strike Global Offensive"
        if (Test-Path $candidate) { $Cs2 = $candidate; break }
    }
}
if (-not $Cs2 -or -not (Test-Path $Cs2)) { Write-Output "CS2 not found. Pass -Cs2 <path>"; exit 1 }

$compiler = Join-Path $Cs2 "game\bin\win64\resourcecompiler.exe"
if (-not (Test-Path $compiler)) { Write-Output "Workshop Tools are not installed (no resourcecompiler.exe)"; exit 1 }

# Sources go to content\, compiled files come out in game\.
$soundDir = Join-Path $Cs2 "content\csgo_addons\$Addon\sounds\zombiemode"
$eventsDir = Join-Path $Cs2 "content\csgo_addons\$Addon\soundevents"
New-Item -ItemType Directory -Force -Path $soundDir, $eventsDir | Out-Null
Copy-Item (Join-Path $src "files\*") $soundDir -Recurse -Force
Copy-Item (Join-Path $src "soundevents\soundevents_addon.vsndevts") $eventsDir -Force

# The addon passport: without it the Workshop tools do not see the folder as an addon.
$gameDir = Join-Path $Cs2 "game\csgo_addons\$Addon"
New-Item -ItemType Directory -Force -Path $gameDir | Out-Null
Copy-Item (Join-Path $src "addoninfo.txt") $gameDir -Force

foreach ($pattern in @("*.wav", "*.mp3", "countdown\*.wav", "zombie\*.wav", "zombie\*.mp3")) {
    & $compiler -i (Join-Path $soundDir $pattern) -r
}
# -f: without it the compiler may answer "skipped" and keep an old result.
& $compiler -i (Join-Path $eventsDir "soundevents_addon.vsndevts") -f -r

$events = Join-Path $gameDir "soundevents\soundevents_addon.vsndevts_c"
if (Test-Path $events) { Write-Output "Built: $gameDir" } else { Write-Output "Sound events were not compiled"; exit 1 }
