<# : batch portion
@echo off
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f='%~f0'; iex ([IO.File]::ReadAllText($f, [Text.Encoding]::UTF8))"
echo.
pause
exit /b
#>

# POLARIS – pilot automaticky (K0 + K6). Dvojklik spúšťa vždy ďalšiu fázu:
#   fáza 1: služba a ikona, recepty (konto žiaka bez hesla s automatickým prihlásením, trezor profilu, Základ, Služby,
#           Slabé PC), ovládače, príprava trezoru → reštart, PC sa samo prihlási ako žiak, ty pripravíš profil
#   fáza 2: uloženie profilu žiaka do trezoru → reštart, PC sa samo prihlási ako žiak (už rýchlo), skúška ako žiak
#   fáza 3: kontrola, že profil sa vrátil z trezoru a program zo Školského obchodu je preč, merania, denník
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
$script:Bad = 0
$AllRecipes = 'ukazka-relacia', 'ukazka-pieskovisko', 'ukazka-zaklad', 'ukazka-sluzby', 'ukazka-slabe-pc'
$Ci = [bool]$env:POLARIS_PILOT_CI

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

function PupilProfile { Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -match "\\$Account$" } }

# čaká, kým Windows pustí profil žiaka (po odhlásení to trvá pár sekúnd); najviac 3 minúty
function WaitForSignOut {
    $waited = 0
    while ((PupilProfile | Where-Object { $_.Loaded }) -and $waited -lt 180) {
        if ($waited -eq 0) { Write-Host '  čakám, kým Windows pustí profil žiaka (najviac 3 minúty) …' }
        Start-Sleep 5
        $waited += 5
    }
    return -not (PupilProfile | Where-Object { $_.Loaded })
}

function Restart($steps) {
    Write-Host ''
    Write-Host 'TERAZ:' -ForegroundColor Cyan
    foreach ($line in $steps) { Write-Host "  $line" }
    Write-Host ''
    if ($Ci) { return } # skúška v CI: bez otázky a bez reštartu
    $answer = Read-Host 'Reštartovať PC teraz? [a/n]'
    if ($answer -match '^(a|ano|áno|y)$') { Restart-Computer -Force }
}

if (-not (Test-Path $Pa)) {
    Write-Host "Nenašiel som $Pa. Rozbaľ celý balík polaris-agent-windows a spusti pilot-k0.cmd z neho." -ForegroundColor Red
    return
}

New-Item -ItemType Directory -Force $State | Out-Null
$phaseNow = if (Test-Path $Phase) { (Get-Content $Phase -Raw).Trim() } else { '0' }
# a newer package than the installed agent: the pilot starts again from phase 1 (the service gets the new programs)
$installed = Join-Path $env:ProgramFiles 'POLARIS\polaris-agent.exe'
function Sha($file) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($file))) }
if ($phaseNow -ne '0' -and (Test-Path $installed) -and (Sha $installed) -ne (Sha $Pa)) {
    Write-Host 'Nové zostavenie POLARIS – pilot začína znova od fázy 1.' -ForegroundColor Cyan
    $phaseNow = '0'
}

if ($phaseNow -eq '3') {
    Write-Host "Pilot je hotový. Záznam: $Log" -ForegroundColor Green
    Write-Host "Ak ho chceš urobiť znova, zmaž priečinok $State a spusti pilot-k0.cmd."
    return
}

if ($phaseNow -eq '0') {
    Set-Content -Path $Log -Value "POLARIS – pilot, fáza 1 – $(Get-Date -Format 'd.M.yyyy HH:mm')" -Encoding UTF8
    Say ("Windows " + [Environment]::OSVersion.Version.ToString(3))

    Step 'Príprava'
    Get-ChildItem -Recurse -File $Root | Unblock-File
    Agent verzia | Out-Null
    New-Item -ItemType Directory -Force 'C:\Users\Public\Documents\Ucivo' | Out-Null

    Step 'Kontrola receptov (nič nemení)'
    foreach ($r in $AllRecipes) {
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

    Step 'Politika hesiel (či dovolí konto bez hesla)'
    $policy = Join-Path $env:TEMP 'polaris-politika.inf'
    secedit /export /cfg $policy /areas SECURITYPOLICY | Out-Null
    foreach ($line in (Get-Content $policy -ErrorAction SilentlyContinue | Where-Object { $_ -match '^(MinimumPasswordLength|PasswordComplexity|PasswordHistorySize)\s*=' })) { Say "  $line" }
    Remove-Item $policy -ErrorAction SilentlyContinue

    Step 'Recepty Relácia, Pieskovisko, Základ, Služby a Slabé PC'
    foreach ($r in $AllRecipes) {
        $code = Agent pouzi (Join-Path $Recipes "$r.json") --ano
        Result ($code -eq 0) "$r použitý"
    }

    Step 'Konto žiaka'
    Result ($null -ne (Get-LocalUser -Name $Account -ErrorAction SilentlyContinue)) "konto $Account je"
    $admins = (Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "\\$Account$" })
    Result (-not $admins) "konto $Account nie je správca"
    $user = Get-LocalUser -Name $Account -ErrorAction SilentlyContinue
    Result ($user -and -not $user.UserMayChangePassword) "žiak si nemôže nastaviť heslo"
    $lsa = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -ErrorAction SilentlyContinue).LimitBlankPasswordUse
    Result ($lsa -eq 1) 'konto bez hesla len priamo pri PC, nie cez sieť'
    Start-Sleep 25
    $winlogon = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Result ($winlogon.AutoAdminLogon -eq '1' -and $winlogon.DefaultUserName -eq $Account) "po štarte sa PC prihlási samo do konta $Account"

    Step 'Ovládače'
    $code = Agent ovladace
    Result ($code -eq 0) 'všetky zariadenia majú ovládač'
    # zálohy ovládačov už nie sú na disku PC (USB kľúč / zdieľaný priečinok, neskôr disk konzoly): stará záloha
    # z predošlého pilota sa zmaže
    $old = 'C:\POLARIS-ovladace'
    if (Test-Path (Join-Path $old 'ovladace.json')) {
        Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue
        Result (-not (Test-Path $old)) "stará záloha ovládačov z disku C: zmazaná"
    }
    $code = Agent ovladace priecinok

    Step 'Príprava trezoru profilu žiaka'
    $code = Agent trezor priprav $Account
    Result ($code -eq 0) 'profil žiaka sa nečistí, kým ho neuložíš'

    Set-Content -Path $Phase -Value '1'
    Say ''
    Say "Fáza 1 hotová, chýb: $script:Bad." $(if ($script:Bad -eq 0) { 'Green' } else { 'Yellow' })
    Restart @(
        '1. PC sa reštartuje a SAMO sa prihlási ako žiak (prvýkrát pomaly – Windows pripravuje profil; POLARIS meria).',
        '2. Priprav profil, ako ho chceš mať pre žiakov: spusti programy, ktoré zvykneš spúšťať vopred, prejdi ich úvodné',
        '   okná, nastav, čo treba. Ostaň prihlásený aspoň 2 minúty (POLARIS meria pamäť).',
        '3. Odhlás sa (Štart -> konto -> Odhlásiť sa), na prihlasovacej obrazovke sa prihlás ako správca a znova dvojklik',
        '   na pilot-k0.cmd – uloží profil do trezoru.')
    return
}

if ($phaseNow -eq '1') {
    Add-Content -Path $Log -Value '' -Encoding UTF8
    Say "POLARIS – pilot, fáza 2 – $(Get-Date -Format 'd.M.yyyy HH:mm')"

    Step 'Uloženie profilu žiaka do trezoru'
    Result (WaitForSignOut) 'žiak je odhlásený'
    $code = Agent trezor ulozit $Account
    Result ($code -eq 0) 'profil žiaka je v trezore'
    Agent trezor | Out-Null

    Set-Content -Path $Phase -Value '2'
    Say ''
    Say "Fáza 2 hotová, chýb: $script:Bad." $(if ($script:Bad -eq 0) { 'Green' } else { 'Yellow' })
    Restart @(
        '1. PC sa reštartuje a SAMO sa prihlási ako žiak – teraz z trezoru, rýchlo.',
        '2. Ako žiak: ikona POLARIS -> Školský obchod -> Audacity (počkaj na hlásenie), na plochu ulož súbor test.txt.',
        '   Pozri: hľadanie v Štarte, Pieskovisko Web a programovanie (internet), sieťový priečinok a tlač,',
        '   Správca úloh -> Aplikácie pri spustení.',
        '3. Ostaň prihlásený aspoň 2 minúty, potom sa odhlás, prihlás sa ako správca a znova dvojklik na pilot-k0.cmd.')
    return
}

# fáza 3
Add-Content -Path $Log -Value '' -Encoding UTF8
Say "POLARIS – pilot, fáza 3 – $(Get-Date -Format 'd.M.yyyy HH:mm')"

Step 'Profil žiaka z trezoru'
Result (WaitForSignOut) 'žiak je odhlásený'
$desktop = "C:\Users\$Account\Desktop\test.txt"
$waited = 0
while ((Test-Path $desktop) -and $waited -lt 120) {
    if ($waited -eq 0) { Write-Host '  čakám, kým služba vráti profil z trezoru (najviac 2 minúty) …' }
    Start-Sleep 5
    $waited += 5
}
Result (-not (Test-Path $desktop)) 'súbor žiaka na ploche zmizol (profil vrátený z trezoru)'
Result ($null -ne (PupilProfile)) 'profil ostal (nevytvára sa znova)'
$audacity = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'Audacity*' }
Result (-not $audacity) 'Audacity zo Školského obchodu je odinštalovaná'
$winlogon = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Result ($winlogon.AutoAdminLogon -eq '1') 'po ďalšom zapnutí sa PC prihlási samo'
Agent trezor | Out-Null

Step 'PC podľa receptov'
foreach ($r in $AllRecipes) {
    $code = Agent kontrola (Join-Path $Recipes "$r.json")
    Result ($code -eq 0) "${r}: PC je podľa receptu"
}

Step 'Služby, ktoré POLARIS zmenil (pôvodný stav)'
Agent sluzby | Out-Null

Step 'Programy, ktoré sa spúšťajú samy po prihlásení'
Agent spustanie | Out-Null

Step 'Meranie prihlásenia (čas do pracovnej plochy, pamäť po 2 minútach)'
Agent meranie 5 | Out-Null

Step 'Denník'
Agent dennik 30 | Out-Null

Set-Content -Path $Phase -Value '3'
Say ''
Say "Pilot hotový, chýb vo fáze 3: $script:Bad." $(if ($script:Bad -eq 0) { 'Green' } else { 'Yellow' })
Write-Host ''
Write-Host "Pošli mi súbor $Log (stačí jeho text) a napíš, čo si videl ako žiak." -ForegroundColor Cyan
