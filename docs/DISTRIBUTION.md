# Сборки, установщик и подготовка к WinGet

## Текущий статус

Репозиторий содержит рабочие инструменты сборки и подготовки манифестов. Это не означает, что пакет уже зарегистрирован в WinGet. Отправка в `microsoft/winget-pkgs` и рассмотрение Microsoft выполняются отдельно.

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

`Package.ps1` создаёт версионные ZIP, setup.exe и `SHA256SUMS.txt`, включает лицензии и сверяет metadata EXE. Если нужен только ZIP, явно укажите `-ZipOnly`; отсутствие компилятора не маскируется успешной сборкой setup.

## Поведение установщика

Установщик не включает автозапуск, не запускает клиент после silent-установки и не закрывает работающие приложения принудительно. Перед обновлением завершите клиент через меню трея. Начатая очередь сохраняется в отдельном пользовательском профиле.

Удаление убирает установленную программу и её uninstall-регистрацию, сохраняя профиль. Настройка автозапуска не меняется установщиком. Если она была включена в приложении, выключите её перед удалением.

`Test-Installer.ps1` предназначен только для временного GitHub-hosted Windows runner. Он отказывается работать на пользовательском ПК, проверяет установку, точные файлы, регистрацию, smoke, удаление и сохранность профиля/автозапуска. Не подделывайте переменные окружения для обхода этой защиты.

## GitHub Actions и релизы

- `ci.yml`: push и Pull Request, минимальные read-only права, сборка/тесты, установщик и установка/удаление на временном Windows runner. Успешный шаг публикует workflow artifact, не GitHub Release.
- `release.yml`: только явно отправленный тег `v<Version>`, совпадающий с `Directory.Build.props`. Сначала повторно выполняется проверенная сборка, затем создаются attestations и Release из проверенных assets.
- Начальный push ветки не создаёт релиз и не отправляет пакет в WinGet.

Проверяйте журнал конкретного запуска Actions. Наличие workflow в репозитории само по себе не означает успешный CI. Подпись приложения не настроена; GitHub attestation подтверждает происхождение сборки, но не отсутствие вредоносного кода и не заменяет Authenticode.

## Манифест WinGet после настоящего релиза

```powershell
. ./scripts/Common.ps1
$metadata = Get-PackageMetadata
$setup = "./artifacts/packages-1/$($metadata.ArtifactStem)-setup.exe"
$url = "$($metadata.RepositoryUrl)/releases/download/v$($metadata.Version)/$($metadata.ArtifactStem)-setup.exe"
./scripts/New-WinGetManifest.ps1 -InstallerPath $setup -Version $metadata.Version -InstallerUrl $url -OutputDirectory ./artifacts/winget-1
winget validate --manifest "./artifacts/winget-1/manifests/t/TJCloud/Client/$($metadata.Version)" --disable-interactivity
```

Генератор требует точную версию и URL assets своего репозитория, скачивает опубликованный установщик и сравнивает SHA-256 с локальным файлом. Он создаёт три YAML схемы 1.12.0. До появления настоящего релиза обычная генерация должна завершиться отказом.

Для проверки только схемы существует `-SchemaOnly`. Такие файлы явно помечены как тестовые; их нельзя публиковать или отправлять в каталог. Успешный `winget validate` не подтверждает установку, доступность URL или регистрацию пакета.

Перед отдельной отправкой нужны: публичный неизменяемый URL релиза, совпадающий SHA-256, проверка установки/обновления/удаления в чистой среде, актуальная проверка безопасности и Pull Request в каталог. См. [официальную процедуру Microsoft](https://learn.microsoft.com/en-us/windows/package-manager/package/repository).
