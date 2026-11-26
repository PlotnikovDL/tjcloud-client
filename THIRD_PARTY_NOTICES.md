# Сторонние компоненты

Собственный код и графика TJCloud Client распространяются по [MIT](LICENSE). Это не меняет лицензии зависимостей. Их версии и content hash зафиксированы в `packages.lock.json`.

| Компонент | Версия | Лицензия по NuGet metadata |
|---|---|---|
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.10 | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.10 | MIT |
| SQLitePCLRaw.bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3 | 2.1.14 | Apache-2.0 |

Self-contained Windows-сборка также содержит .NET Runtime и Windows Desktop Runtime. Условия Microsoft и уведомления о компонентах runtime сохранены в `licenses/` из пакетов runtime 10.0.10. При обновлении runtime обновляйте эти файлы вместе с версией.

Для SQLitePCLRaw сохранены LICENSE и NOTICE из [официального проекта](https://github.com/ericsink/SQLitePCL.raw). Его NOTICE отдельно описывает условия встроенных компонентов SQLite. Для библиотек Microsoft см. [репозиторий .NET](https://github.com/dotnet/dotnet).

Распространяйте `licenses/`, этот файл и корневую LICENSE вместе с бинарной поставкой. Тестовый проект использует ASP.NET Core для loopback HTTPS, но сервер тестов не включается в поставку WPF-приложения.

Компилятор установщика и инструменты GitHub Actions являются инструментами сборки, а не лицензией на код клиента. Перед изменением инструмента упаковки проверяйте его актуальные условия использования.
