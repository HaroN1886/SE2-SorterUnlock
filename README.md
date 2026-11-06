# SorterUnlock — working Conveyor Sorter for Space Engineers 2

**English** · [Русский](#русский)

Unofficial plugin that makes the Conveyor Sorter in Space Engineers 2 actually work: item filter, "Drain all" pump mode, opposite flow, plus a proper filter panel and rules that stop items from bouncing between pumps.

- Game files are **not** modified, everything happens in memory. Remove the launch option and the game is vanilla again.
- Tested on SE2 build **4.0.0.3165**, single player.
- Interface in 13 languages, follows the game language.

### Features
- Filter panel in the sorter terminal: item groups, search, readable names, **Allow selected / Block selected** mode, plain-language hints. Empty list = everything passes.
- "Drain all" pumps items from the sorter's input side to its output side; pumps wake up by themselves after settings change.
- Anti ping-pong rules: no pulling backwards through an active pump, explicit filters take priority on a shared source.
- Refineries and assemblers keep the resources they took for the current job.
- **Transfer mode**: temporarily turns a sorter into an open pipe (10 s – 5 min, or until pumps are done) without touching its settings.
- The player can reach items across the whole conveyor chain regardless of sorters.

### Install
1. Download `SorterUnlock-vX.Y.Z.zip` from [Releases](../../releases) and extract the whole folder outside the game folder, e.g. `C:\Games\SE2Mods\SorterUnlock\`.
2. Steam → Space Engineers 2 → Properties → General → Launch Options:
   ```
   -plugins:C:\Games\SE2Mods\SorterUnlock\SorterUnlock.dll
   ```
3. Start the game. **Back up your save first.**

Full guide: [INSTRUCTIONS_EN.txt](INSTRUCTIONS_EN.txt).

### Limitations
Single player only tested; dedicated servers / co-op clients not tested. May break after game updates (it then logs an error and leaves the game vanilla). See section 8 of the guide.

### Bugs
Open an [Issue](../../issues) and attach `%AppData%\SpaceEngineers2\SorterUnlock\SorterUnlock.log`.

---

## Русский

Неофициальный плагин, который делает Conveyor Sorter в Space Engineers 2 рабочим: фильтр предметов, режим насоса «Слить всё», встречный поток, нормальная панель фильтра и правила, чтобы предметы не бегали между насосами туда-сюда.

- Файлы игры **не меняются**, всё в памяти. Убрал параметр запуска — игра снова ванильная.
- Проверено на сборке SE2 **4.0.0.3165**, одиночная игра.
- Интерфейс на 13 языках, переключается вместе с языком игры.

### Возможности
- Панель фильтра в терминале сортера: группы, поиск, нормальные названия, режим **Пропускать / Блокировать отмеченные**, подсказки простыми словами. Пустой список = проходит всё.
- «Слить всё» качает предметы со стороны входа сортера на сторону выхода; насосы сами просыпаются после изменения настроек.
- Защита от пинг-понга: нельзя тянуть назад через активный насос, явный фильтр имеет приоритет на общем источнике.
- Очистительные заводы и сборщики не теряют ресурсы, взятые под текущую работу.
- **Перекачка**: временно превращает сортер в открытую трубу (10 с – 5 мин или пока насосы не закончат), не трогая его настройки.
- Игрок видит предметы по всей конвейерной цепочке, независимо от сортеров.

### Установка
1. Скачай `SorterUnlock-vX.Y.Z.zip` из [Releases](../../releases) и распакуй всю папку вне папки игры, например в `C:\Games\SE2Mods\SorterUnlock\`.
2. Steam → Space Engineers 2 → Свойства → Общие → Параметры запуска:
   ```
   -plugins:C:\Games\SE2Mods\SorterUnlock\SorterUnlock.dll
   ```
3. Запусти игру. **Сначала сделай копию сохранения.**

Подробная инструкция: [INSTRUCTIONS_RU.txt](INSTRUCTIONS_RU.txt).

### Ограничения
Проверено только в одиночной игре; выделенный сервер и клиенты в кооперативе не проверялись. Может сломаться после обновления игры (тогда пишет ошибку в лог и не трогает игру). Подробнее — раздел 8 инструкции.

### Ошибки
Создай [Issue](../../issues) и приложи `%AppData%\SpaceEngineers2\SorterUnlock\SorterUnlock.log`.

---

License: MIT. Space Engineers 2 is a trademark of Keen Software House; this project is not affiliated with Keen.
