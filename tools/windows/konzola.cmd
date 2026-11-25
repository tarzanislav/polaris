@echo off
rem POLARIS - konzola jednym dvojklikom. Daj tento subor do priecinka s polaris-centrum.exe.
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set MENO=ucitel

if not exist polaris-centrum.exe (
  echo Chyba: polaris-centrum.exe nie je v tomto priečinku: %CD%
  pause & exit /b 1
)

rem 1. kľúč konzoly - len prvýkrát
if not exist %MENO%.kluc (
  echo Vytváram kľúč konzoly "%MENO%". Vymysli heslo, aspoň 12 znakov - pri písaní sa nezobrazuje.
  polaris-centrum.exe kluc novy %MENO%
  if errorlevel 1 ( pause & exit /b 1 )
  echo.
  echo HOTOVO. Súbor %MENO%.verejny.json nakopíruj na server k inštalačnému skriptu.
  echo %MENO%.kluc nikam nekopíruj. Heslo si bezpečne zapíš.
  echo.
)

rem 2. adresa a odtlačok centra - len prvýkrát, uložia sa do centrum.txt
if not exist centrum.txt (
  echo Zadaj údaje, ktoré vypísal inštalačný skript na serveri.
  echo Ak server ešte nie je nainštalovaný, len zatvor toto okno.
  set /p ADRESA=Adresa centra, napr. 192.168.1.10 alebo https://192.168.1.10:8443 : 
  set /p ODTLACOK=Odtlačok centra, 64 znakov: 
)
if not exist centrum.txt (
  call :uloz
)
for /f "usebackq tokens=1,2" %%a in ("centrum.txt") do ( set ADRESA=%%a& set ODTLACOK=%%b )

rem 3. konzola v prehliadači
echo.
echo Spúšťam konzolu - centrum %ADRESA%. Zadaj heslo ku kľúču. Koniec: zatvor toto okno.
polaris-centrum.exe konzola %MENO%.kluc --server %ADRESA% %ODTLACOK%
if errorlevel 1 (
  echo.
  echo Konzola sa nespustila. Ak je zlá adresa alebo odtlačok, zmaž centrum.txt a spusti znova.
)
pause
exit /b 0

:uloz
set A=%ADRESA: =%
if /i not "%A:~0,8%"=="https://" set A=https://%A%:8443
set O=%ODTLACOK: =%
> centrum.txt echo %A% %O%
exit /b 0
