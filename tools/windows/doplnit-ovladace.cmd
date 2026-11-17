<# : batch portion
@echo off
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f='%~f0'; iex ([IO.File]::ReadAllText($f, [Text.Encoding]::UTF8))"
echo.
pause
exit /b
#>

# POLARIS – doplnenie chýbajúcich ovládačov (pomocník pred K-ovládačmi v konzole)
# Nájde zariadenia bez ovládača, hľadá ich v oficiálnom katalógu Microsoft Update, stiahne balík podpísaný
# Microsoftom a nainštaluje ho cez pnputil (Windows prijme len podpísaný ovládač). Nič iné na PC nemení.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Catalog = 'https://www.catalog.update.microsoft.com'
$Work = Join-Path $env:TEMP ('polaris-ovladace-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

function Missing {
    @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -eq 28 })
}

function Text($html) {
    [Net.WebUtility]::HtmlDecode(($html -replace '<[^>]+>', ' ' -replace '\s+', ' ')).Trim()
}

# hľadané výrazy od najpresnejšieho: VEN_8086&DEV_1E3A&SUBSYS_…, potom VEN_8086&DEV_1E3A
function Queries($ids) {
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($id in $ids) {
        $parts = ($id -replace '^[^\\]+\\', '') -split '&'
        for ($n = $parts.Count; $n -ge [Math]::Min(2, $parts.Count); $n--) {
            $q = ($parts[0..($n - 1)] -join '&')
            if ($q.Length -ge 6 -and -not $list.Contains($q)) { $list.Add($q) }
        }
    }
    $list
}

function Search($query) {
    $page = Invoke-WebRequest -UseBasicParsing -Uri "$Catalog/Search.aspx?q=$([uri]::EscapeDataString($query))"
    $html = $page.Content
    $start = $html.IndexOf('ctl00_catalogBody_updateMatches')
    if ($start -lt 0) { return @() }
    $found = foreach ($row in ($html.Substring($start) -split '<tr')) {
        $guid = [regex]::Match($row, '<input[^>]*id="([0-9a-fA-F\-]{36})"').Groups[1].Value
        if (-not $guid) { continue }
        $cells = [regex]::Matches($row, '<td[^>]*>(.*?)</td>', 'Singleline') | ForEach-Object { Text $_.Groups[1].Value }
        if ($cells.Count -lt 6) { continue }
        $date = [datetime]::MinValue
        $d = $cells[4] -split '/'
        if ($d.Count -eq 3) { try { $date = Get-Date -Year $d[2] -Month $d[0] -Day $d[1] } catch { } }
        [pscustomobject]@{ Guid = $guid; Title = $cells[1]; Products = $cells[2]; Date = $date; Version = $cells[5] }
    }
    # novšie Windows a novší dátum najprv
    $found | Sort-Object @{ Expression = { $_.Products -match 'Windows 1[01]' }; Descending = $true }, @{ Expression = 'Date'; Descending = $true }
}

function Links($guid) {
    $post = @{ size = 0; updateID = $guid; uidInfo = $guid } | ConvertTo-Json -Compress
    $dialog = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$Catalog/DownloadDialog.aspx" `
        -ContentType 'application/x-www-form-urlencoded' -Body @{ updateIDs = "[$post]" }
    $content = $dialog.Content.Replace('www.download.windowsupdate', 'download.windowsupdate')
    [regex]::Matches($content, "https?://(?:dl\.delivery\.mp\.microsoft\.com|(?:catalog\.s\.)?download\.windowsupdate\.com)/[^'`"]*") |
        ForEach-Object { $_.Value } | Where-Object { $_ -match '\.cab$' } | Select-Object -Unique
}

function TryInstall($device, $update, $index) {
    $folder = Join-Path $Work "$index"
    New-Item -ItemType Directory -Force $folder | Out-Null
    foreach ($url in (Links $update.Guid)) {
        $cab = Join-Path $folder ([IO.Path]::GetFileName($url))
        Write-Host "    sťahujem $([IO.Path]::GetFileName($url)) …"
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $cab
        $sig = Get-AuthenticodeSignature $cab
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft') {
            Write-Host "    balík nie je platne podpísaný Microsoftom – preskakujem" -ForegroundColor Yellow
            continue
        }
        $out = Join-Path $folder 'subory'
        New-Item -ItemType Directory -Force $out | Out-Null
        & expand.exe -F:* $cab $out | Out-Null
        if (-not (Get-ChildItem $out -Recurse -Filter *.inf)) { Write-Host '    v balíku nie je ovládač (.inf)'; continue }
        Write-Host '    inštalujem …'
        & pnputil.exe /add-driver (Join-Path $out '*.inf') /subdirs /install | Out-Null
        $now = Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -eq $device.PNPDeviceID }
        if ($now -and $now.ConfigManagerErrorCode -eq 0) { return $true }
    }
    $false
}

try {
    Write-Host 'POLARIS – doplnenie chýbajúcich ovládačov' -ForegroundColor Cyan
    $missing = Missing
    if ($missing.Count -eq 0) { Write-Host 'Všetky zariadenia majú ovládač. Nie je čo robiť.' -ForegroundColor Green; return }

    $restore = Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($restore) { Write-Host "Posledný bod obnovenia: $($restore.Description)" }
    try { Checkpoint-Computer -Description 'POLARIS: pred ovládačmi' -RestorePointType DEVICE_DRIVER_INSTALL -WarningAction SilentlyContinue } catch { }

    $ok = 0
    foreach ($device in $missing) {
        Write-Host ''
        Write-Host "Zariadenie: $($device.Name)" -ForegroundColor White
        $done = $false
        foreach ($query in (Queries $device.HardwareID)) {
            $updates = @(Search $query)
            if ($updates.Count -eq 0) { continue }
            Write-Host "  katalóg ($query): $($updates.Count) možností"
            $i = 0
            foreach ($update in ($updates | Select-Object -First 3)) {
                $i++
                Write-Host "  skúšam: $($update.Title) ($($update.Version), $($update.Date.ToString('d.M.yyyy')))"
                if (TryInstall $device $update $i) { $done = $true; break }
            }
            break
        }
        if ($done) { $ok++; Write-Host '  HOTOVO – ovládač nainštalovaný' -ForegroundColor Green }
        else { Write-Host '  nenašiel som vhodný ovládač – treba stránku podpory výrobcu PC' -ForegroundColor Yellow }
    }

    Write-Host ''
    Write-Host "Výsledok: $ok z $($missing.Count) zariadení doplnených. Ak Windows žiada reštart, reštartuj PC." -ForegroundColor Cyan
}
catch {
    Write-Host "CHYBA: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
}
