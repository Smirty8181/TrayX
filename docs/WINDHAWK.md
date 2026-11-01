# Windhawk и модификации Windows

TrayX не использует, не устанавливает, не запускает и не настраивает Windhawk. Страница «Улучшения Windows» показывает наличие записи приложения в списке установленных программ, наличие процесса `windhawk.exe` и официальные ссылки на каталог.

Windhawk и его моды вмешиваются в другие процессы Windows. Это позволяет изменять оболочку и интерфейс, для которых нет поддерживаемых публичных API, но может приводить к несовместимости после обновлений ОС, конфликтам модов, предупреждениям антивируса и проблемам с античитами. Установка и выбор модов остаются ответственностью пользователя.

## Проверенные рекомендации

Названия, страницы каталога и целевые процессы сверены с метаданными официального репозитория `ramensoftware/windhawk-mods` на 1 ноября 2026 года. Ссылки открывают страницы каталога; TrayX не устанавливает моды.

| Мод | Назначение | Целевые процессы из метаданных мода | Риск |
|---|---|---|---|
| [Windows 11 Taskbar Styler](https://windhawk.net/mods/windows-11-taskbar-styler) | Темы Taskbar, включая DockLike, TranslucentTaskbar и SimplyTransparent | `explorer.exe` | Средний |
| [Always show all taskbar tray icons](https://windhawk.net/mods/taskbar-notification-icons-show-all) | Показ всех значков трея Windows 11 | `explorer.exe` | Низкий/средний |
| [Windows 11 Start Menu Styler](https://windhawk.net/mods/windows-11-start-menu-styler) | Стилизация меню «Пуск» и компонентов поиска | `StartMenuExperienceHost.exe`, `SearchHost.exe`, `SearchApp.exe` | Средний |
| [Windows 11 Notification Center Styler](https://windhawk.net/mods/windows-11-notification-center-styler) | Стилизация Центра уведомлений и быстрых действий | `ShellExperienceHost.exe`, `ShellHost.exe` | Средний |
| [Windows 11 File Explorer Styler](https://windhawk.net/mods/windows-11-file-explorer-styler) | Стилизация Проводника | `explorer.exe` | Средний |
| [Taskbar Labels for Windows 11](https://windhawk.net/mods/taskbar-labels) | Подписи и группировка запущенных программ | `explorer.exe` | Средний |

Уровни риска — качественная ориентировочная оценка TrayX, а не гарантия совместимости. Даже мод с ограниченной областью действия изменяет поведение Windows и может зависеть от сборки ОС.

## Рекомендации по безопасности

- Устанавливайте и включайте моды по одному; проверяйте работу после каждого изменения.
- Перед запуском игр с античитом ознакомьтесь с актуальными исключениями Windhawk. Не считайте исключения гарантией совместимости.
- При нестабильности используйте описанный Windhawk Safe Mode (`Ctrl+Win+W`) либо отключите последний включённый мод.
- Антивирус может предупреждать о механизме внедрения кода. Не отключайте защиту ради TrayX.
- Чтобы отменить изменение, отключите/удалите мод в Windhawk и перезапустите Explorer либо Windows штатным способом.

## Визуальный ориентир

Для визуального образца можно вручную выбрать встроенные темы `DockLike`, `TranslucentTaskbar` или `SimplyTransparent` в настройках Taskbar Styler. TrayX не предоставляет импортируемый пресет для этого мода: формат параметров зависит от версии мода, и неподтверждённая конфигурация могла бы нарушить его настройки. Никакие значения реестра Windhawk не меняются.

### Официальные источники

- [Каталог Windhawk](https://windhawk.net/mods)
- [Официальный репозиторий модов](https://github.com/ramensoftware/windhawk-mods)
- [Windhawk Safe Mode](https://windhawk.net/)
