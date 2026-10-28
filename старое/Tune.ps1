#requires -RunAsAdministrator
# PC-Tune - Dell Latitude 5420 / i5-1135G7 / Windows 11
# Запускать через Tune.bat. Меню: ничего не делается без явного выбора.
# Пункт 1 только читает и ничего не меняет.

$ErrorActionPreference = 'Continue'
$Host.UI.RawUI.WindowTitle = 'PC-Tune'
$BackupKey = 'HKCU:\Software\PCTune\RunBackup'
$LogFile   = Join-Path $PSScriptRoot 'PC-Tune.log'

function Say  ($m) { Write-Host $m -ForegroundColor Gray }
function Ok   ($m) { Write-Host "  [+] $m" -ForegroundColor Green }
function Warn ($m) { Write-Host "  [!] $m" -ForegroundColor Yellow }
function Bad  ($m) { Write-Host "  [x] $m" -ForegroundColor Red }
function Head ($m) { Write-Host ''; Write-Host "== $m" -ForegroundColor Cyan }
function Log  ($m) { '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m | Add-Content $LogFile -Encoding UTF8 }

function Ask ($q) {
    Write-Host ''
    $a = Read-Host "$q [y/N]"
    return ($a -eq 'y' -or $a -eq 'Y' -or $a -eq 'д' -or $a -eq 'Д')
}

function Run ($exe, $argline) {
    Log "RUN $exe $argline"
    $p = Start-Process -FilePath $exe -ArgumentList $argline -NoNewWindow -Wait -PassThru
    return $p.ExitCode
}

function Get-Median ($values) {
    $s = @($values | Sort-Object)
    if (-not $s.Count) { return 0 }
    $m = [int]($s.Count / 2)
    if ($s.Count % 2) { return $s[$m] }
    return ($s[$m - 1] + $s[$m]) / 2
}

# Один проход фиксированного объёма вычислений. Возвращает миллисекунды.
function Measure-Pass ($iter) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $x = 0.0
    for ($i = 0; $i -lt $iter; $i++) { $x = [math]::Sqrt($x + 1.0) }
    $sw.Stop()
    return $sw.Elapsed.TotalMilliseconds
}

# Троттлинг меряется выполненной работой, а не показаниями датчиков.
# На этой машине счётчики частоты врут (см. Test-PerfCounters), поэтому
# сравниваем, сколько одинаковой работы успевает процессор в начале и в конце
# прогрева. Просела скорость — значит система сбрасывает частоту.
function Test-Throttling {
    $threads = (Get-CimInstance Win32_Processor | Select-Object -First 1).NumberOfLogicalProcessors

    Say '  Подбираю размер задачи...'
    $iter = 20000
    while ((Measure-Pass $iter) -lt 120 -and $iter -lt 20000000) { $iter *= 2 }

    Say "  Гружу $threads потоков. Вентилятор зашумит — так и надо."
    $load = @()
    for ($i = 0; $i -lt $threads; $i++) {
        $load += Start-Process powershell.exe -PassThru -WindowStyle Hidden `
            -ArgumentList '-NoProfile', '-Command', '$e=(Get-Date).AddSeconds(45); $x=0.0; while((Get-Date) -lt $e){ $x=[math]::Sqrt($x+1) }'
    }
    try {
        # Фоновые процессы стартуют не мгновенно. Первые секунды машина ещё
        # недогружена и считает быстрее — если взять их за точку отсчёта,
        # тест объявит троттлингом обычный выход нагрузки на режим.
        Say '  Жду, пока нагрузка выйдет на режим...'
        $warm = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $warm) { Measure-Pass $iter | Out-Null }

        $times = @()
        $stop  = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $stop) {
            $ms = Measure-Pass $iter
            $times += $ms
            Write-Host ("`r  проход {0,2}: {1,6:N0} мс   " -f $times.Count, $ms) -NoNewline -ForegroundColor DarkGray
        }
        Write-Host ''

        if ($times.Count -lt 8) { Warn 'Проходов слишком мало для вывода'; return }

        # Медиана, а не среднее: отдельные проходы улетают вдвое вверх, когда
        # планировщик отдаёт ядро фоновой нагрузке. Среднее такие всплески тащат.
        $q     = [math]::Max(3, [int]($times.Count / 4))
        $first = Get-Median $times[0..($q - 1)]
        $last  = Get-Median $times[($times.Count - $q)..($times.Count - 1)]
        $slow  = [math]::Round(($last / $first - 1) * 100)

        Say ('  В начале : {0:N0} мс на проход (медиана)' -f $first)
        Say ('  В конце  : {0:N0} мс на проход (медиана)' -f $last)
        if ($slow -ge 35) {
            Bad ('К концу прогрева машина замедлилась на {0}%. Очень похоже на троттлинг.' -f $slow)
            Say '  Процессор упирается в нагрев или в лимит мощности. Помогут пункт 5,'
            Say '  чистка радиатора и замена термопасты.'
        }
        elseif ($slow -ge 15) {
            Warn ('Замедление на {0}%. Возможен сброс частоты под долгой нагрузкой.' -f $slow)
        }
        else {
            Ok ('Скорость держится ровно (разница {0}%), троттлинга не видно' -f $slow)
        }
        Write-Host ''
        Say '  Тест грубый: он не отличает нагрев от борьбы за ядра, а датчики'
        Say '  температуры на этой машине закрыты. Точный ответ даст только HWiNFO64'
        Say '  с графиком CPU Package Temperature под той же нагрузкой.'
    }
    finally {
        foreach ($p in $load) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        Ok 'Нагрузка снята'
    }
}

# Счётчики производительности Windows умеют ломаться: набор \Processor пропадает,
# и тогда диспетчер задач, powercfg /energy и любые мониторы показывают чушь.
function Test-PerfCounters {
    Head 'Счётчики производительности'
    $broken = $false
    try {
        Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop | Out-Null
        Ok 'Набор \Processor на месте'
    }
    catch {
        Bad 'Набор \Processor отсутствует — счётчики повреждены'
        $broken = $true
    }
    if (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009')) {
        Bad 'Нет ключа Perflib\009 с английскими именами счётчиков'
        $broken = $true
    }
    if (-not $broken) { return }

    Say '  Из-за этого встроенные измерители частоты и энергопотребления врут:'
    Say '  система сообщает фиксированную частоту и «100% от максимума» даже'
    Say '  под полной нагрузкой. Лечится перестроением базы счётчиков.'
    if (Ask 'Перестроить счётчики (lodctr /R)? Займёт секунды, нужна перезагрузка.') {
        Run 'lodctr.exe' '/R' | Out-Null
        Run 'cmd.exe' '/c winmgmt /resyncperf' | Out-Null
        Ok 'Счётчики перестроены. Проверь после перезагрузки этим же пунктом.'
        Log 'perf counters rebuilt'
    }
}

# ---------------------------------------------------------------- 1. ДИАГНОСТИКА
function Invoke-Diagnose {
    Head 'Железо'
    $cs   = Get-CimInstance Win32_ComputerSystem
    $cpu  = Get-CimInstance Win32_Processor | Select-Object -First 1
    $bios = Get-CimInstance Win32_BIOS
    Say ('  Модель      : {0} {1}' -f $cs.Manufacturer, $cs.Model)
    Say ('  Процессор   : {0} ({1} ядер / {2} потоков)' -f $cpu.Name.Trim(), $cpu.NumberOfCores, $cpu.NumberOfLogicalProcessors)
    Say ('  BIOS        : {0} от {1}' -f $bios.SMBIOSBIOSVersion, (Get-Date $bios.ReleaseDate -Format 'dd.MM.yyyy'))

    Head 'Память'
    $mods  = @(Get-CimInstance Win32_PhysicalMemory)
    $slots = (Get-CimInstance Win32_PhysicalMemoryArray | Select-Object -First 1).MemoryDevices
    foreach ($m in $mods) {
        Say ('  {0,-8} {1,5} ГБ  {2} МТ/с  {3}' -f $m.DeviceLocator, [math]::Round($m.Capacity / 1GB), $m.ConfiguredClockSpeed, "$($m.PartNumber)".Trim())
    }
    if ($mods.Count -ge 2) { Ok 'Двухканальный режим — встроенная графика работает на полную' }
    else { Bad "Одна планка из $slots. Вторая такая же даст iGPU +20..30%. Это главный апгрейд машины." }

    Head 'Накопитель'
    foreach ($d in Get-PhysicalDisk) {
        Say ('  {0}  {1}  {2}  {3} ГБ' -f $d.FriendlyName, $d.MediaType, $d.BusType, [math]::Round($d.Size / 1GB))
        $rc = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
        if ($rc) {
            if ($null -ne $rc.Wear)        { Say ('     износ ячеек : {0}%' -f $rc.Wear) }
            if ($null -ne $rc.Temperature) { Say ('     температура : {0} C' -f $rc.Temperature) }
            if ($rc.ReadErrorsUncorrected -gt 0 -or $rc.WriteErrorsUncorrected -gt 0) {
                Bad 'Неисправимые ошибки чтения/записи — делай резервную копию'
            }
        }
        if ($d.HealthStatus -ne 'Healthy') { Bad ('Состояние: ' + $d.HealthStatus) } else { Ok 'Состояние: здоров' }
    }
    $v = Get-Volume C
    $freePct = [math]::Round($v.SizeRemaining / $v.Size * 100)
    Say ('  Свободно C: : {0} ГБ ({1}%)' -f [math]::Round($v.SizeRemaining / 1GB), $freePct)
    if ($freePct -lt 15) { Bad 'Меньше 15% свободно — SSD замедляется, Windows начинает тормозить' }

    $trim = (fsutil behavior query DisableDeleteNotify) -join ' '
    if ($trim -match 'NTFS DisableDeleteNotify = 0') { Ok 'TRIM включён' } else { Warn 'TRIM выключен' }

    Head 'Питание'
    Say ('  ' + ((powercfg /getactivescheme) -join ''))
    $b = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($b) { Say ('  Батарея     : заряд {0}%' -f $b.EstimatedChargeRemaining) }

    Head 'Безопасность против скорости'
    $dg = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue
    if ($dg) {
        if ($dg.SecurityServicesRunning -contains 2) { Warn 'Memory Integrity (HVCI) ВКЛЮЧЕН — стоит 5..10% процессора' }
        else { Ok 'Memory Integrity выключен — производительность не срезана' }
    }

    Head 'Автозагрузка'
    Get-CimInstance Win32_StartupCommand | ForEach-Object { Say ('  {0,-18} {1}' -f $_.Name, $_.Location) }

    Head 'Драйверы с проблемами'
    $bad = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -ne 0 })
    if ($bad.Count) { foreach ($x in $bad) { Bad ('{0} (код {1})' -f $x.Name, $x.ConfigManagerErrorCode) } }
    else { Ok 'Устройств с ошибками нет' }

    Head 'Частота'
    # MaxClockSpeed тут возвращает не паспортную частоту, а ту же, что и текущую,
    # поэтому номинал берём из названия процессора и сравниваем с заявленным.
    $nominal = 0
    if ($cpu.Name -match '([\d.]+)\s*GHz') { $nominal = [int]([double]$Matches[1] * 1000) }
    Say ('  Система сообщает: {0} МГц' -f $cpu.CurrentClockSpeed)
    if ($nominal) {
        Say ('  Паспортная базовая: {0} МГц' -f $nominal)
        if ($cpu.MaxClockSpeed -eq $cpu.CurrentClockSpeed -and $cpu.MaxClockSpeed -lt $nominal) {
            Warn 'Заявленный максимум равен текущей частоте и ниже паспортной —'
            Say  '  верный признак того, что показаниям частоты верить нельзя.'
        }
    }
    Warn 'Температуру процессора Dell через WMI не отдаёт. Нужен HWiNFO64 или Core Temp.'

    Test-PerfCounters

    if (Ask 'Прогнать нагрузочный тест на 25 секунд? Он меряет реальную скорость работы, а не показания датчиков.') {
        Test-Throttling
    }

    if (Ask 'Собрать отчёт по батарее и энергопотреблению в HTML?') {
        $rep = Join-Path $PSScriptRoot 'battery-report.html'
        $slp = Join-Path $PSScriptRoot 'energy-report.html'
        powercfg /batteryreport /output $rep | Out-Null
        Ok "Износ батареи: $rep"
        Say '  Снимаю 60-секундный энергопрофиль, не трогай ноутбук...'
        powercfg /energy /output $slp /duration 60 | Out-Null
        Ok "Что мешает сну и жрёт заряд: $slp"
    }
}

# ---------------------------------------------------------------- 2. ТОЧКА ВОССТАНОВЛЕНИЯ
function New-Checkpoint {
    Head 'Точка восстановления'
    try {
        Enable-ComputerRestore -Drive 'C:\' -ErrorAction SilentlyContinue
        # Windows отказывается делать точки чаще раза в сутки — снимаем ограничение
        New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' `
            -Name 'SystemRestorePointCreationFrequency' -Value 0 -PropertyType DWord -Force | Out-Null
        Checkpoint-Computer -Description "PC-Tune $(Get-Date -Format 'dd.MM HH:mm')" -RestorePointType MODIFY_SETTINGS
        Ok 'Точка создана. Откат системы: rstrui.exe'
        Log 'checkpoint created'
    }
    catch {
        Bad "Не вышло: $($_.Exception.Message)"
        Warn 'Скорее всего выключена защита системы. Включить: SystemPropertiesProtection.exe'
    }
}

# ---------------------------------------------------------------- 3. ДРАЙВЕРЫ И BIOS
function Update-DriversAndBios {
    Head 'Драйверы и BIOS через Dell Command | Update'
    $dcu = @(
        'C:\Program Files\Dell\CommandUpdate\dcu-cli.exe',
        'C:\Program Files (x86)\Dell\CommandUpdate\dcu-cli.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $dcu) {
        Bad 'dcu-cli.exe не найден'
        Say '  Поставить:  winget install Dell.CommandUpdate'
        return
    }
    Ok "Найден: $dcu"

    $scanLog = Join-Path $PSScriptRoot 'dcu-scan.log'
    Say '  Сканирую каталог Dell: BIOS, чипсет, Thunderbolt, Wi-Fi, видео...'
    Run $dcu ('/scan -outputLog="{0}"' -f $scanLog) | Out-Null
    if (Test-Path $scanLog) {
        Get-Content $scanLog -Encoding Unicode -ErrorAction SilentlyContinue |
            Select-Object -Last 40 | ForEach-Object { Say "  $_" }
    }

    Warn 'Обновление BIOS требует питания от сети и перезагрузки. Не выдёргивай шнур.'
    if (Ask 'Ставить все найденные обновления, включая BIOS?') {
        $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($bat -and $bat.BatteryStatus -eq 1) {
            Bad 'Ноутбук работает от батареи. Подключи зарядку и повтори этот пункт.'
            return
        }
        Run $dcu ('/applyUpdates -reboot=disable -outputLog="{0}"' -f (Join-Path $PSScriptRoot 'dcu-apply.log')) | Out-Null
        Ok 'Готово. Нужна перезагрузка — сделай её вручную, когда будет удобно.'
        Log 'dcu applyUpdates done'
    }

    if (Ask 'Обновить заодно установленные программы (winget upgrade --all)?') {
        Run 'winget' 'upgrade --all --silent --accept-source-agreements --accept-package-agreements' | Out-Null
        Ok 'Программы обновлены'
    }
}

# ---------------------------------------------------------------- 4. ПИТАНИЕ И ОТКЛИК
function Set-PowerProfile {
    Head 'Питание: от сети — на полную, от батареи — щадяще'

    $SUB_PROC  = '54533251-82be-4824-96c1-47b60b740d00'
    $PROCMIN   = '893dee8e-2bef-41e0-89c6-b55d0929964c'
    $PROCMAX   = 'bc5038f7-23e0-4960-96da-33abaf5935ec'
    $BOOSTMODE = 'be337238-0d82-4146-a960-4f3749d470c7'
    $SUB_USB   = '2a737441-1930-4402-8d77-b2bebba308a3'
    $USBSUSP   = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'

    # --- от сети
    powercfg /setacvalueindex SCHEME_CURRENT $SUB_PROC $PROCMIN   5    | Out-Null
    powercfg /setacvalueindex SCHEME_CURRENT $SUB_PROC $PROCMAX   100  | Out-Null
    powercfg /setacvalueindex SCHEME_CURRENT $SUB_PROC $BOOSTMODE 2    | Out-Null  # агрессивный турбобуст
    powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK  DISKIDLE   0    | Out-Null  # NVMe не парковать
    powercfg /setacvalueindex SCHEME_CURRENT $SUB_USB  $USBSUSP   0    | Out-Null  # USB не усыплять
    powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM  0    | Out-Null
    powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE  900  | Out-Null
    powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 0   | Out-Null
    Ok 'От сети: потолок частоты, агрессивный буст, без парковки диска и USB'

    # --- от батареи
    powercfg /setdcvalueindex SCHEME_CURRENT $SUB_PROC $PROCMIN   5    | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT $SUB_PROC $PROCMAX   100  | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT $SUB_PROC $BOOSTMODE 1    | Out-Null  # буст мягкий
    powercfg /setdcvalueindex SCHEME_CURRENT $SUB_USB  $USBSUSP   1    | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM  2    | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE  180  | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 900 | Out-Null
    Ok 'От батареи: экономия шины и экрана, потолок частоты сохранён'

    powercfg /setactive SCHEME_CURRENT | Out-Null
    Log 'power profile applied'

    Head 'Аппаратное планирование GPU'
    $gk  = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
    $cur = (Get-ItemProperty $gk -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode
    if ($cur -eq 2) { Ok 'Уже включено' }
    elseif (Ask 'Включить HAGS? Iris Xe поддерживает, снижает задержку ввода. Нужна перезагрузка.') {
        Set-ItemProperty $gk -Name HwSchMode -Value 2 -Type DWord
        Ok 'Включено, применится после перезагрузки'
        Log 'HAGS enabled'
    }

    Head 'Визуальные эффекты'
    if (Ask 'Отключить анимации окон? Интерфейс станет отзывчивее, сглаживание шрифтов останется.') {
        Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Name VisualFXSetting -Value 3 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name MinAnimate -Value '0' -Force -ErrorAction SilentlyContinue
        Ok 'Применится после перезахода в систему'
        Log 'visual effects off'
    }
}

# ---------------------------------------------------------------- 5. ТЕПЛОВОЙ РЕЖИМ
function Get-Cctk {
    return @(
        'C:\Program Files (x86)\Dell\Command Configure\X86_64\cctk.exe',
        'C:\Program Files\Dell\Command Configure\X86_64\cctk.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Open-ThermalSettings {
    Head 'Тепловой режим и настройки BIOS'
    Say '  На Latitude 5420 множитель процессора заблокирован, а андервольт закрыт'
    Say '  защитой от Plundervolt в BIOS Dell. Разгона тут нет и не будет — ни'
    Say '  скриптом, ни утилитой. Рычаг здесь один: тепловой профиль, он задаёт'
    Say '  лимиты мощности и то, насколько рано раскручивается вентилятор.'

    $cctk = Get-Cctk
    if (-not $cctk) {
        Warn 'Dell Command | Configure не установлен — режим не прочитать из консоли.'
        Say '  Поставить:  winget install Dell.CommandConfigure'
        Say '  Или вручную: Dell Power Manager -> Тепловое управление -> Ultra Performance'
        try {
            $dpm = Get-StartApps -ErrorAction Stop | Where-Object { $_.Name -match 'Power Manager' } | Select-Object -First 1
            if ($dpm) { Start-Process ('shell:AppsFolder\' + $dpm.AppID); Ok 'Dell Power Manager открыт' }
        }
        catch { }
        return
    }

    Head 'Что сейчас в BIOS'
    foreach ($opt in 'ThermalManagement', 'TurboMode', 'SpeedShift', 'Speedstep', 'CStatesCtrl') {
        $v = (& $cctk "--$opt" 2>&1 | Select-String '=') -join ''
        if ($v) { Say ('  ' + $v.Trim()) }
    }

    $cur = (& $cctk '--ThermalManagement' 2>&1 | Select-String 'ThermalManagement=(\S+)')
    if ("$cur" -match 'UltraPerformance') {
        Ok 'Тепловой режим уже на максимуме — поднимать некуда'
    }
    else {
        Warn 'Тепловой режим не максимальный.'
        Say '  Optimized — баланс, Quiet — тише и медленнее, Cool — холоднее корпус,'
        Say '  UltraPerformance — раньше раскручивает вентилятор и держит лимиты выше.'
        if (Ask 'Поставить UltraPerformance?') {
            & $cctk '--ThermalManagement=UltraPerformance' 2>&1 | ForEach-Object { Say "  $_" }
            Ok 'Задано'
            Log 'thermal set to UltraPerformance'
        }
    }

    Write-Host ''
    Say '  Управление вентилятором BIOS этой модели наружу не отдаёт — принудительно'
    Say '  раскрутить его штатными средствами нельзя.'
    Write-Host ''
    Say '  Физика, которую софт не обойдёт: машина включена впервые в мае 2021.'
    Say '  За такой срок термопаста высыхает, а радиатор забивается пылью. Чистка'
    Say '  и замена пасты снимают троттлинг надёжнее любой настройки — на 5420'
    Say '  это десять винтов снизу.'

    if (Ask 'Выгрузить все 130 настроек BIOS в файл для просмотра?') {
        $f = Join-Path $PSScriptRoot 'bios-settings.txt'
        & $cctk -o $f 2>&1 | Out-Null
        Ok "Готово: $f"
    }
}

# ---------------------------------------------------------------- 6. ОЧИСТКА
function Clear-Junk {
    Head 'Очистка'
    $before = (Get-Volume C).SizeRemaining

    Say '  Временные файлы...'
    foreach ($p in @($env:TEMP, "$env:WINDIR\Temp")) {
        Get-ChildItem $p -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    Ok 'Temp очищен'

    Say '  Кэш Windows Update...'
    Stop-Service wuauserv, bits -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$env:WINDIR\SoftwareDistribution\Download" -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Start-Service wuauserv, bits -ErrorAction SilentlyContinue
    Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue
    Ok 'Кэш обновлений очищен'

    Say '  Корзина...'
    Clear-RecycleBin -Force -ErrorAction SilentlyContinue
    Ok 'Корзина пуста'

    Warn 'Сжатие WinSxS необратимо: откатить установленные обновления станет нельзя.'
    if (Ask 'Сжать хранилище компонентов? Освобождает 3-8 ГБ, занимает 5-15 минут.') {
        Run 'dism.exe' '/Online /Cleanup-Image /StartComponentCleanup /ResetBase' | Out-Null
        Ok 'WinSxS сжат'
    }

    Say '  TRIM накопителя...'
    Optimize-Volume -DriveLetter C -ReTrim -ErrorAction SilentlyContinue
    Ok 'TRIM выполнен'

    $freed = [math]::Round(((Get-Volume C).SizeRemaining - $before) / 1GB, 2)
    Head ("Освобождено: {0} ГБ" -f $freed)
    Log "cleanup freed ${freed}GB"
}

# ---------------------------------------------------------------- 7. ЦЕЛОСТНОСТЬ
function Repair-Windows {
    Head 'Проверка целостности Windows'
    Say '  DISM чинит образ, sfc — системные файлы по этому образу. Порядок важен:'
    Say '  sfc без здорового образа чинить нечем.'
    Say '  Минут десять-двадцать. Работать можно, выключать нельзя.'
    Run 'dism.exe' '/Online /Cleanup-Image /RestoreHealth' | Out-Null
    Ok 'DISM отработал'
    Run 'sfc.exe' '/scannow' | Out-Null
    Ok 'sfc отработал'
    Say '  Подробности, если что-то нашлось: %WINDIR%\Logs\CBS\CBS.log'
    Log 'sfc+dism done'
}

# ---------------------------------------------------------------- 8. АВТОЗАГРУЗКА
function Edit-Startup {
    Head 'Автозагрузка'
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $items  = @()
    $props  = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
    if ($props) {
        foreach ($p in $props.PSObject.Properties) {
            if ($p.Name -like 'PS*') { continue }
            $items += [pscustomobject]@{ Name = $p.Name; Value = $p.Value }
        }
    }
    if (-not $items.Count) { Warn 'Пользовательских записей нет'; return }

    for ($i = 0; $i -lt $items.Count; $i++) {
        Say ('  [{0}] {1,-14} {2}' -f ($i + 1), $items[$i].Name, $items[$i].Value)
    }
    Write-Host ''
    Say '  Эти программы стартуют вместе с Windows и держат память постоянно.'
    Say '  Отключение их не удаляет — запустишь вручную, когда понадобятся.'
    Say '  Вернуть обратно: пункт R.'
    $sel = Read-Host '  Номера через запятую (Enter — ничего не трогать)'
    if (-not $sel) { return }

    if (-not (Test-Path $BackupKey)) { New-Item $BackupKey -Force | Out-Null }
    foreach ($n in ($sel -split ',')) {
        $idx = 0
        if (-not [int]::TryParse($n.Trim(), [ref]$idx)) { continue }
        if ($idx -lt 1 -or $idx -gt $items.Count) { continue }
        $it = $items[$idx - 1]
        Set-ItemProperty $BackupKey -Name $it.Name -Value $it.Value -Force
        Remove-ItemProperty $runKey -Name $it.Name -Force
        Ok ('Отключено: ' + $it.Name)
        Log ('startup disabled: ' + $it.Name)
    }
}

# ---------------------------------------------------------------- 9. СЕТЬ
function Reset-Network {
    Head 'Сеть'
    ipconfig /flushdns | Out-Null
    Ok 'Кэш DNS сброшен'
    netsh int tcp set global autotuninglevel=normal | Out-Null
    Ok 'Автонастройка окна TCP возвращена в normal'
    if (Ask 'Полный сброс стека (winsock + IP)? Лечит странные обрывы, требует перезагрузки.') {
        netsh winsock reset | Out-Null
        netsh int ip reset | Out-Null
        Ok 'Стек сброшен. Нужна перезагрузка.'
        Log 'network stack reset'
    }
}

# ---------------------------------------------------------------- G. ИГРЫ
# Всё, что здесь, применено 21.09.2026. Пункт нужен, чтобы повторить твики
# после отката системы, переустановки Windows или добавить новую игру.
function Set-GameTweaks {
    Head 'Твики под старые DX9-игры'

    function SetReg ($path, $name, $value, $type) {
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $type -Force | Out-Null
    }

    SetReg 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled' 0 DWord
    SetReg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled' 0 DWord
    Ok 'Game DVR и фоновая запись экрана выключены'

    # Windows 11 сделала timeBeginPeriod локальным для процесса. Старые игры
    # рассчитывают на глобальный эффект — без этого ключа они дёргаются.
    SetReg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' 'GlobalTimerResolutionRequests' 1 DWord
    Ok 'Глобальное разрешение таймера включено (нужна перезагрузка)'

    $mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
    SetReg $mm 'SystemResponsiveness'   10 DWord   # фону 10% вместо 20%
    SetReg $mm 'NetworkThrottlingIndex' -1 DWord   # 0xFFFFFFFF — без ограничения
    SetReg "$mm\Tasks\Games" 'GPU Priority'        8      DWord
    SetReg "$mm\Tasks\Games" 'Priority'            6      DWord
    SetReg "$mm\Tasks\Games" 'Scheduling Category' 'High' String
    SetReg "$mm\Tasks\Games" 'SFIO Priority'       'High' String
    Ok 'MMCSS: игре отдан приоритет процессора, GPU и дискового ввода-вывода'

    # Nagle склеивает мелкие пакеты ради экономии трафика. В MMO это лишние
    # десятки миллисекунд на каждом действии.
    $act = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1
    if ($act) {
        $ifp = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($act.InterfaceGuid)"
        SetReg $ifp 'TcpAckFrequency' 1 DWord
        SetReg $ifp 'TCPNoDelay'      1 DWord
        Ok "Алгоритм Нейгла отключён на адаптере $($act.Name)"
    }
    else { Warn 'Активный адаптер не найден, настройка сети пропущена' }

    # Параметры скрыты в powercfg по умолчанию — сначала снимаем флаг.
    $SUB = '54533251-82be-4824-96c1-47b60b740d00'
    $CPMIN = '0cc5b647-c1df-4637-891a-dec35c318583'
    $CPMAX = 'ea062031-0e34-4ff1-9b6d-eb1059334028'
    powercfg -attributes $SUB $CPMIN -ATTRIB_HIDE 2>&1 | Out-Null
    powercfg -attributes $SUB $CPMAX -ATTRIB_HIDE 2>&1 | Out-Null
    powercfg /setacvalueindex SCHEME_CURRENT $SUB $CPMIN 100 2>&1 | Out-Null
    powercfg /setacvalueindex SCHEME_CURRENT $SUB $CPMAX 100 2>&1 | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT $SUB $CPMIN 50  2>&1 | Out-Null
    powercfg /setactive SCHEME_CURRENT 2>&1 | Out-Null
    Ok 'Парковка ядер от сети отключена — поток игры не кочует между ядрами'

    Add-GameExe
}

# Отключает «оптимизации во весь экран» — прослойку DWM, которую Windows 10/11
# навязывает полноэкранным DX9-приложениям. Для старых игр это лишний кадр
# задержки ввода и рывки при смене сцены.
function Add-GameExe {
    Head 'Клиенты игр'
    $layers = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
    if (-not (Test-Path $layers)) { New-Item $layers -Force | Out-Null }

    $known = @(
        'C:\Games\LineageII Essence\system\Frost\LineageII.exe',
        'C:\Games\RF-Default\rf-default.exe'
    )
    foreach ($e in $known) {
        if (Test-Path $e) {
            New-ItemProperty -Path $layers -Name $e -Value '~ DISABLEDXMAXIMIZEDWINDOWEDMODE' -PropertyType String -Force | Out-Null
            Ok ('Оптимизации во весь экран отключены: ' + (Split-Path $e -Leaf))
        }
    }

    Write-Host ''
    $more = Read-Host '  Путь к ещё одному .exe (Enter — пропустить)'
    if ($more) {
        $more = $more.Trim('"')
        if (Test-Path $more) {
            New-ItemProperty -Path $layers -Name $more -Value '~ DISABLEDXMAXIMIZEDWINDOWEDMODE' -PropertyType String -Force | Out-Null
            Ok ('Добавлено: ' + (Split-Path $more -Leaf))
            Log "layer added: $more"
        }
        else { Bad 'Файл не найден' }
    }

    Write-Host ''
    Say '  Главное, чего не сделает ни один твик: разрешение. Монитор у тебя'
    Say '  3440x1440 — это 4.95 млн пикселей, вдвое с лишним больше, чем 1080p.'
    Say '  Iris Xe — встроенная графика. Поставь в самой игре 1920x1080'
    Say '  (или 2560x1080 под ультраширокий) — это даст больше кадров,'
    Say '  чем всё остальное в этом меню вместе взятое.'
}

# ---------------------------------------------------------------- V. ВИДЕО В ИГРАХ
# Самый весомый пункт всего меню: на встроенной графике кадры съедают пиксели
# и полноэкранные эффекты, а не настройки Windows.
function Set-IniValue ($file, $key, $value) {
    $txt = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::Default)
    if ($txt -notmatch "(?m)^\s*$([regex]::Escape($key))\s*=") { return $false }
    $new = [regex]::Replace($txt, "(?m)^(\s*$([regex]::Escape($key))\s*=).*$", "`${1}$value")
    if ($new -eq $txt) { return $false }
    [System.IO.File]::WriteAllText($file, $new, [System.Text.Encoding]::Default)
    return $true
}

function Get-IniValue ($file, $key) {
    $txt = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::Default)
    if ($txt -match "(?m)^\s*$([regex]::Escape($key))\s*=\s*(.*)$") { return $Matches[1].Trim() }
    return $null
}

function Backup-Ini ($file) {
    $bak = "$file.pctune.bak"
    if (-not (Test-Path $bak)) { Copy-Item $file $bak -Force }
    return $bak
}

function Set-GameVideo {
    Head 'Настройки видео в самих играх'
    Say '  Здесь выигрыш больше, чем от всего остального меню вместе взятого.'
    Say '  Встроенная Iris Xe упирается в число пикселей и полноэкранные эффекты.'

    foreach ($proc in 'rf-default', 'LineageII') {
        if (Get-Process $proc -ErrorAction SilentlyContinue) {
            Bad "Игра $proc сейчас запущена. Закрой её и повтори — иначе она перезапишет файл."
            return
        }
    }

    # ---------- RF Online ----------
    $rf = 'C:\Games\RF-Default\R3Engine.ini'
    if (Test-Path $rf) {
        Head 'RF Online'
        Say ('  Сейчас: {0}x{1}, динамический свет {2}, дальность обзора {3}' -f `
            (Get-IniValue $rf 'ScreenXSize'), (Get-IniValue $rf 'ScreenYSize'),
            (Get-IniValue $rf 'DynamicLight'), (Get-IniValue $rf 'SeeDistance'))
        Write-Host ''
        Say '  1920x1080 вместо 2560x1440 — это на 44% меньше пикселей на кадр.'
        Say '  Картинка станет чуть мельче, частота кадров вырастет заметно.'
        if (Ask 'Понизить разрешение RF Online до 1920x1080?') {
            Backup-Ini $rf | Out-Null
            if (Set-IniValue $rf 'ScreenXSize' 1920) { Ok 'ScreenXSize = 1920' }
            if (Set-IniValue $rf 'ScreenYSize' 1080) { Ok 'ScreenYSize = 1080' }
            Log 'RF resolution lowered'
        }
        Write-Host ''
        Say '  Динамический свет 3 — максимум. На старом движке он считается'
        Say '  процессором, а не видеокартой, и бьёт именно по однопоточности.'
        if (Ask 'Снизить динамический свет до 1?') {
            Backup-Ini $rf | Out-Null
            if (Set-IniValue $rf 'DynamicLight' 1) { Ok 'DynamicLight = 1' }
            Log 'RF dynamic light lowered'
        }
    }

    # ---------- Lineage II ----------
    $l2 = 'C:\Games\LineageII Essence\system\Option.ini'
    if (Test-Path $l2) {
        Head 'Lineage II Essence'
        Say ('  Сейчас: {0}x{1}, текстуры {2}, эффекты {3}' -f `
            (Get-IniValue $l2 'GamePlayViewportX'), (Get-IniValue $l2 'GamePlayViewportY'),
            (Get-IniValue $l2 'TextureDetail'), (Get-IniValue $l2 'EffectDetail'))
        Ok 'Разрешение и детализация уже на минимуме — тут трогать нечего'
        Write-Host ''
        Say '  А вот пост-обработка Yebis включена вся: свечение, размытие по'
        Say '  глубине и сглаживание. Это полноэкранные проходы, самые дорогие'
        Say '  операции для встроенной графики. Картинка станет резче и проще.'
        if (Ask 'Отключить Yebis (свечение, DOF, сглаживание) и тени персонажей?') {
            Backup-Ini $l2 | Out-Null
            foreach ($kv in @(@('YebisGlow', '0'), @('YebisDOF', 'False'),
                              @('YebisAntiAliasing', 'False'), @('PawnShadow', 'False'))) {
                if (Set-IniValue $l2 $kv[0] $kv[1]) { Ok ('{0} = {1}' -f $kv[0], $kv[1]) }
            }
            Log 'L2 yebis off'
        }
        Write-Host ''
        $cur = Get-IniValue $l2 'RENDERCHARACTERCOUNT'
        Say "  Предел отрисовки персонажей сейчас $cur."
        Warn 'Это не только про кадры: меньше предел — меньше видно игроков'
        Warn 'вокруг. В осаде и массовом PvP это влияет на то, что ты успеешь'
        Warn 'заметить. Снижай, только если в городе невозможно играть.'
        if (Ask 'Снизить предел до 50 персонажей?') {
            Backup-Ini $l2 | Out-Null
            if (Set-IniValue $l2 'RENDERCHARACTERCOUNT' 50) { Ok 'RENDERCHARACTERCOUNT = 50' }
            Log 'L2 character count lowered'
        }
    }

    Write-Host ''
    Say '  Копии исходных файлов лежат рядом с оригиналами: *.pctune.bak'
    Say '  Вернуть — переименовать обратно, или пункт R.'
}

# ---------------------------------------------------------------- S. КОМПРОМИССЫ
# Каждый пункт здесь меняет безопасность на скорость. Поэтому спрашивается
# отдельно, с объяснением цены.
function Set-SecurityTradeoffs {
    Head 'Скорость ценой безопасности'
    Warn 'Каждый пункт ниже что-то отключает. Читай цену, а не только выгоду.'

    Write-Host ''
    Say '  1. Исключения Defender для папок игр.'
    Say '     Выгода: антивирус перестаёт проверять сотни файлов ресурсов при'
    Say '     загрузке локаций — меньше рывков и обращений к диску.'
    Say '     Цена: вирус, попавший в эти папки, останется незамеченным.'
    Say '     Клиенты приватных серверов — именно та категория, где это бывает.'
    if (Ask 'Исключить папки игр из проверки Defender?') {
        foreach ($d in 'C:\Games\LineageII Essence', 'C:\Games\RF-Default') {
            if (Test-Path $d) {
                try { Add-MpPreference -ExclusionPath $d -ErrorAction Stop; Ok "Исключено: $d" ; Log "defender exclusion: $d" }
                catch { Bad ('Не вышло: ' + $_.Exception.Message) }
            }
        }
    }

    Write-Host ''
    Say '  2. Политика телеметрии на минимум.'
    Say '     Службы DiagTrack и dmwappushservice на этой машине уже отключены,'
    Say '     но политика задаёт уровень сбора для остальных компонентов.'
    Say '     Цена: небольшая, на Pro-редакции это поддерживаемая настройка.'
    if (Ask 'Выставить AllowTelemetry = 0?') {
        $p = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'
        if (-not (Test-Path $p)) { New-Item $p -Force | Out-Null }
        New-ItemProperty -Path $p -Name 'AllowTelemetry' -Value 0 -PropertyType DWord -Force | Out-Null
        Ok 'AllowTelemetry = 0'
        Log 'telemetry policy set'
    }

    Write-Host ''
    Say '  3. Задачи сбора статистики в планировщике.'
    Say '     Просыпаются по расписанию и шуршат диском. Для игры безвредны,'
    Say '     но на медленном SSD дают лишние обращения в неподходящий момент.'
    if (Ask 'Отключить задачи CEIP и Application Experience?') {
        $tasks = @(
            @('Consolidator', '\Microsoft\Windows\Customer Experience Improvement Program\'),
            @('UsbCeip', '\Microsoft\Windows\Customer Experience Improvement Program\'),
            @('ProgramDataUpdater', '\Microsoft\Windows\Application Experience\'),
            @('Microsoft Compatibility Appraiser', '\Microsoft\Windows\Application Experience\')
        )
        foreach ($t in $tasks) {
            try {
                Disable-ScheduledTask -TaskName $t[0] -TaskPath $t[1] -ErrorAction Stop | Out-Null
                Ok ('Отключено: ' + $t[0])
            }
            catch { Say ('  пропущено: ' + $t[0]) }
        }
        Log 'ceip tasks disabled'
    }

    Write-Host ''
    Say '  4. Виртуализационная защита (VBS).'
    Say '     Сейчас гипервизор загружен, хотя ни одна служба защиты на нём не'
    Say '     работает — то есть накладные расходы есть, а пользы почти нет.'
    Say '     Выгода: несколько процентов процессора, заметнее всего в старых'
    Say '     однопоточных играх.'
    Bad '     Цена: перестанут работать WSL2, Docker Desktop, песочница Windows'
    Bad '     и эмуляторы Android. Если пользуешься чем-то из этого — не трогай.'
    if (Ask 'Выключить VBS и гипервизор?') {
        $p = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard'
        if (-not (Test-Path $p)) { New-Item $p -Force | Out-Null }
        New-ItemProperty -Path $p -Name 'EnableVirtualizationBasedSecurity' -Value 0 -PropertyType DWord -Force | Out-Null
        Run 'bcdedit.exe' '/set hypervisorlaunchtype off' | Out-Null
        Ok 'Выключено. Вступит в силу после перезагрузки.'
        Say '  Вернуть: bcdedit /set hypervisorlaunchtype auto'
        Log 'VBS disabled'
    }
}

# ---------------------------------------------------------------- R. ОТКАТ
function Undo-Changes {
    Head 'Откат'
    if (Ask 'Вернуть схемы питания к заводским?') {
        powercfg -restoredefaultschemes | Out-Null
        Ok 'Схемы питания сброшены'
    }

    if (Test-Path $BackupKey) {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $b = Get-ItemProperty $BackupKey
        foreach ($p in $b.PSObject.Properties) {
            if ($p.Name -like 'PS*') { continue }
            Set-ItemProperty $runKey -Name $p.Name -Value $p.Value -Force
            Ok ('Автозагрузка возвращена: ' + $p.Name)
        }
        Remove-Item $BackupKey -Recurse -Force
    }
    else { Say '  Отключённых записей автозагрузки нет' }

    $gk = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
    if ((Get-ItemProperty $gk -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode -eq 2) {
        if (Ask 'Выключить аппаратное планирование GPU обратно?') {
            Set-ItemProperty $gk -Name HwSchMode -Value 1 -Type DWord
            Ok 'Выключено'
        }
    }

    if (Ask 'Откатить игровые твики (таймер, MMCSS, Nagle, слои совместимости)?') {
        $mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
        Remove-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' -Name 'GlobalTimerResolutionRequests' -Force -ErrorAction SilentlyContinue
        Set-ItemProperty $mm -Name 'SystemResponsiveness' -Value 20 -Type DWord -ErrorAction SilentlyContinue
        Set-ItemProperty "$mm\Tasks\Games" -Name 'Priority' -Value 2 -Type DWord -ErrorAction SilentlyContinue
        Set-ItemProperty "$mm\Tasks\Games" -Name 'Scheduling Category' -Value 'Medium' -ErrorAction SilentlyContinue
        Set-ItemProperty "$mm\Tasks\Games" -Name 'SFIO Priority' -Value 'Normal' -ErrorAction SilentlyContinue
        Ok 'Таймер и MMCSS возвращены к значениям Windows'

        $act = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1
        if ($act) {
            $ifp = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($act.InterfaceGuid)"
            Remove-ItemProperty $ifp -Name 'TcpAckFrequency' -Force -ErrorAction SilentlyContinue
            Remove-ItemProperty $ifp -Name 'TCPNoDelay' -Force -ErrorAction SilentlyContinue
            Ok 'Алгоритм Нейгла возвращён'
        }

        $layers = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
        foreach ($e in @('C:\Games\LineageII Essence\system\Frost\LineageII.exe', 'C:\Games\RF-Default\rf-default.exe')) {
            Remove-ItemProperty $layers -Name $e -Force -ErrorAction SilentlyContinue
        }
        Ok 'Оптимизации во весь экран возвращены'
        Log 'game tweaks reverted'
    }

    if (Ask 'Вернуть исключения Defender (снова проверять папки игр)?') {
        foreach ($d in 'C:\Games\LineageII Essence', 'C:\Games\RF-Default') {
            try { Remove-MpPreference -ExclusionPath $d -ErrorAction Stop; Ok "Исключение снято: $d" } catch { }
        }
    }

    $baks = @(Get-ChildItem 'C:\Games' -Recurse -Filter '*.pctune.bak' -ErrorAction SilentlyContinue)
    if ($baks.Count -and (Ask "Вернуть настройки видео в играх из копий ($($baks.Count) шт.)?")) {
        foreach ($b in $baks) {
            $orig = $b.FullName -replace '\.pctune\.bak$', ''
            Copy-Item $b.FullName $orig -Force
            Remove-Item $b.FullName -Force
            Ok ('Восстановлен: ' + (Split-Path $orig -Leaf))
        }
        Log 'game ini restored'
    }

    Warn 'Сжатие WinSxS и удалённые файлы не откатываются. Для них — точка восстановления: rstrui.exe'
    Log 'undo done'
}

# ---------------------------------------------------------------- МЕНЮ
function Show-Menu {
    Clear-Host
    Write-Host ''
    Write-Host '  PC-Tune' -ForegroundColor White -NoNewline
    Write-Host '   Latitude 5420 / i5-1135G7' -ForegroundColor DarkGray
    Write-Host '  ---------------------------------------------' -ForegroundColor DarkGray
    Write-Host '  1  Диагностика           ' -NoNewline; Write-Host 'только читает' -ForegroundColor DarkGray
    Write-Host '  2  Точка восстановления  ' -NoNewline; Write-Host 'сделать до правок' -ForegroundColor DarkGray
    Write-Host '  3  Драйверы и BIOS       ' -NoNewline; Write-Host 'Dell Command Update' -ForegroundColor DarkGray
    Write-Host '  4  Питание и отклик      ' -NoNewline; Write-Host 'powercfg, HAGS, анимации' -ForegroundColor DarkGray
    Write-Host '  5  Тепловой режим        ' -NoNewline; Write-Host 'Dell Power Manager' -ForegroundColor DarkGray
    Write-Host '  6  Очистка диска         ' -NoNewline; Write-Host 'temp, WinSxS, TRIM' -ForegroundColor DarkGray
    Write-Host '  7  Целостность Windows   ' -NoNewline; Write-Host 'DISM + sfc' -ForegroundColor DarkGray
    Write-Host '  8  Автозагрузка          ' -NoNewline; Write-Host 'показать и отключить' -ForegroundColor DarkGray
    Write-Host '  9  Сеть                  ' -NoNewline; Write-Host 'DNS, TCP, winsock' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  V  Видео в играх          ' -NoNewline -ForegroundColor White; Write-Host 'разрешение и эффекты — главный выигрыш' -ForegroundColor DarkGray
    Write-Host '  G  Игры: RF Online, L2    ' -NoNewline -ForegroundColor White; Write-Host 'таймер, MMCSS, Nagle, свой .exe' -ForegroundColor DarkGray
    Write-Host '  S  Скорость ценой защиты  ' -NoNewline -ForegroundColor Yellow; Write-Host 'Defender, телеметрия, VBS' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  A  Всё по порядку: 2-3-4-6-7' -ForegroundColor White
    Write-Host '  R  Откат изменений' -ForegroundColor Yellow
    Write-Host '  0  Выход' -ForegroundColor DarkGray
    Write-Host ''
}

while ($true) {
    Show-Menu
    $c = (Read-Host '  Выбор').Trim().ToUpper()
    switch ($c) {
        '1' { Invoke-Diagnose }
        '2' { New-Checkpoint }
        '3' { Update-DriversAndBios }
        '4' { Set-PowerProfile }
        '5' { Open-ThermalSettings }
        '6' { Clear-Junk }
        '7' { Repair-Windows }
        '8' { Edit-Startup }
        '9' { Reset-Network }
        'V' { Set-GameVideo }
        'G' { Set-GameTweaks }
        'S' { Set-SecurityTradeoffs }
        'A' { New-Checkpoint; Update-DriversAndBios; Set-PowerProfile; Clear-Junk; Repair-Windows }
        'R' { Undo-Changes }
        '0' { return }
        default { Warn 'Нет такого пункта' }
    }
    Write-Host ''
    Read-Host '  Enter — вернуться в меню' | Out-Null
}
