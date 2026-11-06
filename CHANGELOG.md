# Changelog

## 0.6.2
- Interface in 13 game languages; follows the game language live.
- Optional `SorterUnlock.lang.json` to override texts.

## 0.6.1
- Empty item list = no filtering, in both modes.
- Transfer mode turns the sorter into a fully open pipe (filter and direction ignored) without touching its settings.

## 0.6.0
- Pumps never pull backwards through an active pump.
- A pump that explicitly lists an item takes it first from a shared source.
- Transfer mode ("Перекачка") with timer, +30 s, restore now and auto-finish when pumps are idle.
- Pumps restart automatically after filter / mode changes.

## 0.4.x
- Filter mode drop-down ("Allow selected" / "Block selected") replaces the stock whitelist toggle.

## 0.3.x
- Own filter panel: groups, search, readable names, plain-language hints.
- Drain respects production reservations (refineries, assemblers).
- Drain moves items only across the sorter.

## 0.2.x
- Drain All and opposite flow work; sorter gets an output side.
- Ping-pong fix; player access to the whole conveyor chain.
- Saved filter items are restored on load; sorter terminal tab syncs again.

## 0.1.0
- Prototype: the game's own FilterComponent attached to the Conveyor Sorter in memory.
