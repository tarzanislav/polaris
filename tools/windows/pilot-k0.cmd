<# : batch portion
@echo off
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f='%~f0'; iex ([IO.File]::ReadAllText($f, [Text.Encoding]::UTF8))"
echo.
pause
exit /b
#>

# POLARIS – pilot K0 automaticky. Dvojklik spúšťa vždy ďalšiu fázu:
#   fáza 1: konto žiaka, kontrola receptov, služba a ikona, recepty Relácia, Pieskovisko a Základ, ovládače
#   (potom reštart a krátka skúška ako žiak – asi 5 minút ručne)
#   fáza 2: kontrola, že profil žiaka sa vyčistil a program zo Školského obchodu je preč, výpis denníka
# Všetko ide do súboru POLARIS-pilot.txt na ploche – ten mi pošli. Názov PC ani hardvér sa do neho nezapisujú.

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Root = Split-Path -Parent $f
$Pa = Join-Path $Root 'out\agent\polaris-agent.exe'
$Recipes = Join-Path $Root 'recepty'
$State = Join-Path $env:ProgramData 'POLARIS-pilot'
$Phase = Join-Path $State 'faza.txt'
$Log = Join-Path ([Environment]::GetFolderPath('Desktop')) 'POLARIS-pilot.txt'
$Account = 'ziak'
$Password = 'Skola-2026' # heslo nesmie obsahovať meno konta (pravidlo zložitosti Windows)
$script:Bad = 0

function Say($text, $color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    Add-Content -Path $Log -Value $text -Encoding UTF8
}

function Step($title) { Say ''; Say "=== $title ===" 'Cyan' }

function Result($ok, $text) {
    if ($ok) { Say "  OK    $text" 'Green' } else { Say "  CHYBA $text" 'Red'; $script:Bad++ }
}

# polaris-agent s argumentmi, výstup do záznamu; vráti kód skončenia
function Agent {
    $out = & $Pa @args 2>&1 | Out-String
    $code = $LASTEXITCODE
    Say ("> polaris-agent " + ($args -join ' ') + "   [kód $code]") 'DarkGray'
    foreach ($line in ($out.TrimEnd() -split "`r?`n")) { Say "  $line" }
    return $code
}

if (-not (Test-Path $Pa)) {
    Write-Host "Nenašiel som $Pa. Rozbaľ celý balík polaris-agent-windows a spusti pilot-k0.cmd z neho." -ForegroundColor Red
    return
}

New-Item -ItemType Directory -Force $State | Out-Null
$phaseNow = if (Test-Path $Phase) { (Get-Content $Phase -Raw).Trim() } else { '0' }
# a newer package than the installed agent: the pilot starts again from phase 1 (the service gets the new programs)
$installed = Join-Path $env:ProgramFiles 'POLARIS\polaris-agent.exe'
if ($phaseNow -ne '0' -and (Test-Path $installed) -and (Get-FileHash $installed).Hash -ne (Get-FileHash $Pa).Hash) {
    Write-Host 'Nové zostavenie POLARIS – pilot začína znova od fázy 1.' -ForegroundColor Cyan
    $phaseNow = '0'
}

if ($phaseNow -eq '2') {
    Write-Host "Pilot je hotový. Záznam: $Log" -ForegroundColor Green
    Write-Host "Ak ho chceš urobiť znova, zmaž priečinok $State a spusti pilot-k0.cmd."
    return
}

if ($phaseNow -eq '0') {
    Set-Content -Path $Log -Value "POLARIS – pilot K0, fáza 1 – $(Get-Date -Format 'd.M.yyyy HH:mm')" -Encoding UTF8
    Say ("Windows " + [Environment]::OSVersion.Version.ToString(3))

    Step 'Príprava'
    Get-ChildItem -Recurse -File $Root | Unblock-File
    Agent verzia | Out-Null
    New-Item -ItemType Directory -Force 'C:\Users\Public\Documents\Ucivo' | Out-Null

    Step 'Konto žiaka'
    if (Get-LocalUser -Name $Account -ErrorAction SilentlyContinue) {
        Say "  konto $Account už existuje (heslo nemením)"
    }
    else {
        $out = & net user $Account $Password /add /passwordchg:no 2>&1 | Out-String
        Result ($null -ne (Get-LocalUser -Name $Account -ErrorAction SilentlyContinue)) "konto $Account vytvorené $($out.Trim() -replace '\s+', ' ')"
    }
    $admins = (Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "\\$Account$" })
    Result (-not $admins) "konto $Account nie je správca"

    Step 'Kontrola receptov (nič nemení)'
    foreach ($r in 'ukazka-relacia', 'ukazka-pieskovisko', 'ukazka-zaklad') {
        $code = Agent overit (Join-Path $Recipes "$r.json")
        Result ($code -eq 0) "$r je v poriadku"
    }

    Step 'Služba POLARIS agent a ikona'
    $code = Agent instalovat-sluzbu
    Result ($code -eq 0) 'služba nainštalovaná'
    Start-Sleep 3
    Result ((Get-Service PolarisAgent -ErrorAction SilentlyContinue).Status -eq 'Running') 'služba beží'
    Start-Sleep 5
    $icon = Get-Process polaris-ziak -ErrorAction SilentlyContinue | Select-Object -First 1
    Result ($null -ne $icon) 'ikona beží v tejto relácii'
    if ($icon) {
        Stop-Process -Id $icon.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep 8
        $again = Get-Process polaris-ziak -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $icon.Id } | Select-Object -First 1
        Result ($null -ne $again) 'strážca: ukončenú ikonu spustil znova do 8 s'
    }

    Step 'Recepty Relácia, Pieskovisko a Základ'
    foreach ($r in 'ukazka-relacia', 'ukazka-pieskovisko', 'ukazka-zaklad') {
        $code = Agent pouzi (Join-Path $Recipes "$r.json") --ano
        Result ($code -eq 0) "$r použitý"
    }

    Step 'Ovládače'
    $code = Agent ovladace
    Result ($code -eq 0) 'všetky zariadenia majú ovládač'
    $backup = 'C:\POLARIS-ovladace'
    if (-not (Test-Path $backup)) {
        $code = Agent ovladace zaloha $backup --meno ETEST
        Result ($code -eq 0) "záloha ovládačov v $backup"
        $code = Agent ovladace overit $backup
        Result ($code -eq 0) 'záloha overená'
    }

    Set-Content -Path $Phase -Value '1'
    Say ''
    Say "Fáza 1 hotová, chýb: $script:Bad." $(if ($script:Bad -eq 0) { 'Green' } else { 'Yellow' })
    Write-Host ''
    Write-Host 'TERAZ (asi 5 minút):' -ForegroundColor Cyan
    Write-Host '  1. PC sa reštartuje (dokončí Pieskovisko a pracovnú skupinu).'
    Write-Host "  2. Prihlás sa ako .\$Account s heslom $Password (alebo tvojím, ak konto už bolo)."
    Write-Host '  3. Klikni na ikonu POLARIS pri hodinách -> Školský obchod -> Audacity a počkaj na hlásenie o inštalácii.'
    Write-Host '     (Ak chceš: Správca úloh -> Podrobnosti -> polaris-ziak.exe -> Ukončiť úlohu má hlásiť Prístup odmietnutý.)'
    Write-Host '  4. ODHLÁS sa (Štart -> konto -> Odhlásiť sa), prihlás sa ako správca a znova dvojklik na pilot-k0.cmd.'
    Write-Host ''
    if ($env:POLARIS_PILOT_CI) { return } # skúška v CI: bez otázky a bez reštartu
    $answer = Read-Host 'Reštartovať PC teraz? [a/n]'
    if ($answer -match '^(a|ano|áno|y)$') { Restart-Computer -Force }
    return
}

# fáza 2
Add-Content -Path $Log -Value '' -Encoding UTF8
Say "POLARIS – pilot K0, fáza 2 – $(Get-Date -Format 'd.M.yyyy HH:mm')"

Step 'Čistenie relácie žiaka'
$userProfile = Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -match "\\$Account$" }
$waited = 0
while ($userProfile -and $waited -lt 180) {
    if ($waited -eq 0) { Write-Host '  čakám, kým služba vyčistí profil žiaka (najviac 3 minúty) …' }
    Start-Sleep 10
    $waited += 10
    $userProfile = Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -match "\\$Account$" }
}
Result (-not $userProfile) "profil konta $Account je zmazaný"
$audacity = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'Audacity*' }
Result (-not $audacity) 'Audacity zo Školského obchodu je odinštalovaná'

Step 'PC podľa receptov'
foreach ($r in 'ukazka-relacia', 'ukazka-pieskovisko', 'ukazka-zaklad') {
    $code = Agent kontrola (Join-Path $Recipes "$r.json")
    Result ($code -eq 0) "${r}: PC je podľa receptu"
}

Step 'Denník'
Agent dennik 20 | Out-Null

Set-Content -Path $Phase -Value '2'
Say ''
Say "Pilot hotový, chýb vo fáze 2: $script:Bad." $(if ($script:Bad -eq 0) { 'Green' } else { 'Yellow' })
Write-Host ''
Write-Host "Pošli mi súbor $Log (stačí jeho text) a napíš, čo si videl ako žiak: ikona, inštalácia Audacity, Správca úloh." -ForegroundColor Cyan
