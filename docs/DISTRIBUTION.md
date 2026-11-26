# Сборки, установщик и подготовка к WinGet

## Текущий статус

Готовые установщики, portable ZIP и контрольные суммы публикуются в [GitHub Releases](https://github.com/PlotnikovDL/tjcloud-client/releases). Подача манифеста в `microsoft/winget-pkgs` и рассмотрение Microsoft выполняются после выпуска.

Для проверки до выпуска выберите успешный [Windows build для master](https://github.com/PlotnikovDL/tjcloud-client/actions/workflows/ci.yml?query=branch%3Amaster) и скачайте артефакт `windows-packages`. Требуется вход в GitHub. Артефакты хранятся 14 дней. [Инструкция по скачиванию](../README.md#как-скачать).

- Издатель: `TJCloud`.
- Название: `TJ Cloud Client`.
- Предлагаемый идентификатор WinGet: `TJCloud.Client`. Он не зарезервирован до принятия пакета.
- Версия, издатель, название и URL репозитория: `Directory.Build.props`.
- Постоянный Inno AppId: в `packaging/TjCloudClient.iss`. Не генерируйте его заново для каждой версии.
- Scope установки: текущий пользователь, без повышения прав.
- Бинарники: `%LOCALAPPDATA%/Programs/TJCloudClient`.
- Профиль и очередь: `%LOCALAPPDATA%/TJCloudClient`, отдельно от бинарников.

Основная проверяемая клиентская платформа - Windows 11 x64. Технический минимум установщика не означает поддержку всех устаревших редакций Windows: учитывайте [политику поддерживаемых ОС .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).

## Проверка и упаковка

Команды выполняются из корня репозитория на Windows. Нужны SDK из `global.json`, Python для packaging-тестов и GitHub CLI для проверки происхождения компилятора. Скрипты совместимы с Windows PowerShell 5.1. Используйте новые пустые каталоги вывода, чтобы исключить старые файлы.

```powershell
./scripts/Build.ps1 -ArtifactsRoot ./artifacts/build-1
./scripts/Get-InnoCompiler.ps1 -ToolRoot ./artifacts/inno-6.7.4
./scripts/Package.ps1 -PublishDirectory ./artifacts/build-1/publish -OutputDirectory ./artifacts/packages-1 -IsccPath ./artifacts/inno-6.7.4/compiler/ISCC.exe
```

`Build.ps1` выполняет packaging-тесты, locked restore, Release build, полный native-набор тестов, self-contained publish и smoke с проверкой app-local runtime. Для явных путей к инструментам есть `-DotNet` и `-Python`.

`Get-InnoCompiler.ps1` скачивает официальный Inno Setup 6.7.4. До запуска проверяются закреплённый SHA-256, действительная Authenticode-подпись издателя Pyrsys B.V. и `gh release verify-asset`. Только после этого официальный установщик запускается в собственном portable-режиме в указанном каталоге. Это не глобальная установка компилятора.

`Package.ps1` создаёт версионные ZIP, setup.exe и `SHA256SUMS.txt`, включает лицензии и сверяет metadata EXE. Если нужен только ZIP, явно укажите `-ZipOnly`. Отсутствие компилятора не маскируется успешной сборкой setup.

## Поведение установщика

Установщик не включает автозапуск, не запускает клиент после обычной или silent-установки и не закрывает работающие приложения принудительно. Для ручного обновления завершите клиент через меню трея. Начатая очередь сохраняется в отдельном пользовательском профиле.

Единственное исключение для запуска - точный параметр `/RESTARTCLIENT=1` от согласованного процесса автообновления. Он разрешает один запуск фиксированного `{app}\TjCloudClient.exe` после успешной установки, с рабочим каталогом `{app}` и правами исходного пользователя. Другие или повторяющиеся параметры перезапуска отклоняются. Признаки ожидаемой перезагрузки Windows блокируют этот запуск. Установщик не инициирует перезагрузку ОС. Обычный WinGet не передаёт этот параметр.

Удаление убирает установленную программу и её uninstall-регистрацию, сохраняя профиль. Настройка автозапуска не меняется установщиком. Если она была включена в приложении, выключите её перед удалением.

`Test-Installer.ps1` предназначен только для временного GitHub-hosted Windows runner. Он отказывается работать на пользовательском ПК, проверяет установку, точные файлы, регистрацию, smoke, удаление и сохранность профиля/автозапуска. Не подделывайте переменные окружения для обхода этой защиты.

## GitHub Actions и релизы

- `ci.yml`: push, Pull Request и `workflow_call`, минимальные read-only права. Постоянные имена обязательных проверок - `policy` и `build`. Сначала проверяется политика, затем выполняются сборка/тесты, упаковка и установка/удаление на временном Windows runner. Успешный запуск публикует workflow artifact, не GitHub Release.
- `policy`: PR в `develop` только из `feature/*`, PR в `master` только из `develop` того же репозитория. Изменение целевой ветки PR повторно запускает проверку. Для тега проверяются точная центральная версия и `git merge-base --is-ancestor` относительно `origin/master` после checkout полной истории.
- `release.yml`: только явно отправленный владельцем тег `v<Version>`, совпадающий с `Directory.Build.props`, на коммите из истории `master`. Сначала повторно выполняются `policy` и сборка, затем отдельный job с правами публикации создаёт attestations и Release из проверенных assets. В PR нет доступа к release-секретам или прав записи.
- Начальный push ветки не создаёт релиз и не отправляет пакет в WinGet.

Проверяйте журнал конкретного запуска Actions. Наличие workflow в репозитории само по себе не означает успешный CI. Подпись приложения не настроена. GitHub attestation подтверждает происхождение сборки, но не отсутствие вредоносного кода и не заменяет Authenticode.

Используются закреплённые официальные Actions с Node 24: checkout 7.0.1, upload-artifact 7.0.2 и download-artifact 8.0.2. Hosted runner `windows-2025` должен поддерживать Node 24, минимальная версия runner для этого runtime - 2.327.1. Артефакты загружаются архивом, скачиваются с распаковкой и отказом при несовпадении digest.

## Автоматическое обновление

Автообновление доступно в каталоге, зарегистрированном установщиком для текущего пользователя. Клиент из portable ZIP обновляется вручную. CI проверяет распакованный ZIP и установленный EXE в соответствующих режимах.

- Генератор `NetSparkleUpdater.Tools.AppCastGenerator` закреплён на `2.9.0`, устанавливается только в отдельный tool-path, не глобально. `tools/ReleaseTools.csproj` с locked dependencies независимо проверяет результат.
- Закрытый 32-байтовый seed поступает только в `SPARKLE_PRIVATE_KEY` шага подписи из защищённого environment `release`, после одобрения владельца. Открытый ключ берётся из `TjClient.Wpf/Updates/UpdatePublicKey.txt`. Ключи не передаются в аргументах и не выводятся в журналы.
- После всех изменений setup, включая будущую Authenticode-подпись, создаются `appcast.xml` и `appcast.xml.signature`. В enclosure хранится Ed25519-подпись установщика. Проверяются точные версия, URL, размер, `windows-x64`, подпись setup и отдельная подпись XML. Выход генератора с кодом 0 сам по себе недостаточен.
- `SHA256SUMS.txt` покрывает окончательные setup, ZIP, XML и подпись XML, без самохеша. Публикуются ровно пять файлов: эти четыре файла и SHA256SUMS. Attestation создаётся по окончательным байтам. Workflow скачивает опубликованные assets обратно и повторно проверяет подписи, состав и хеши.
- Постоянный адрес feed: `https://github.com/PlotnikovDL/tjcloud-client/releases/latest/download/appcast.xml`. До первого одобренного выпуска файл по этому адресу не считается доступным. Публикация файлов не транзакционна, поэтому временная неполнота должна приводить к отказу клиента от установки, а не к обходу подписи.
- В обычном CI используются только случайные тестовые seed в памяти. Реальный CLI генератора и установка/перезапуск запускаются лишь на временном hosted runner. Генератор создаёт свой каталог пользователя даже при переданных env-ключах, поэтому локальные тесты не запускают его в профиле разработчика.

`Test-Installer.ps1` проверяет установку и удаление пакета. `Test-AutoUpdateE2E.ps1` запускает установщик с тестовым клиентом на базе исходников WPF, Core и Updates, проверяет сохранение очереди при включённом наблюдении и на паузе, а также отказ от подменённого пакета. Результат трёх сценариев для конкретного коммита записывается в `updater-e2e/hosted-result.json` артефакта `windows-evidence`.

## Манифест WinGet после настоящего релиза

```powershell
. ./scripts/Common.ps1
$metadata = Get-PackageMetadata
$setup = "./artifacts/packages-1/$($metadata.ArtifactStem)-setup.exe"
$url = "$($metadata.RepositoryUrl)/releases/download/v$($metadata.Version)/$($metadata.ArtifactStem)-setup.exe"
./scripts/New-WinGetManifest.ps1 -InstallerPath $setup -Version $metadata.Version -InstallerUrl $url -OutputDirectory ./artifacts/winget-1
winget validate --manifest "./artifacts/winget-1/manifests/t/TJCloud/Client/$($metadata.Version)" --disable-interactivity
```

Генератор скачивает установщик по URL указанного выпуска, сравнивает его SHA-256 с локальным файлом и создаёт три YAML схемы 1.28.0. Политика конфиденциальности берётся из тега той же версии.

Для проверки только схемы существует `-SchemaOnly`. Такие файлы явно помечены как тестовые. Их нельзя публиковать или отправлять в каталог. Успешный `winget validate` не подтверждает установку, доступность URL или регистрацию пакета.

После публикации запускайте [Verify WinGet manifest](https://github.com/PlotnikovDL/tjcloud-client/actions/workflows/winget.yml) на теге выпуска. На временной Windows-машине он проверит манифест, установит пакет через WinGet, запустит клиент с тестовым профилем и проверит удаление. Артефакт `winget-manifests` содержит проверенные YAML для отдельного PR в каталог. См. [порядок подачи Microsoft](https://learn.microsoft.com/en-us/windows/package-manager/package/repository).
