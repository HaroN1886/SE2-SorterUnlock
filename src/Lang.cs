using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;

namespace SorterUnlock;

/// <summary>
/// 0.6.2: UI texts in all 13 game languages. Follows the game's current language (LocalizationManager.CurrentCulture)
/// and reports changes. Optional overrides: SorterUnlock.lang.json next to the DLL, { "ru-RU": { "title": "..." } }.
/// Group names reuse the game's own official translations where a matching key exists.
/// </summary>
internal static class Lang
{
    public const string Default = "en-US";
    public static event Action? Changed;
    private static Dictionary<string, Dictionary<string, string>>? _overrides;
    private static bool _hooked;

    public static string Culture
    {
        get
        {
            try { return Singleton<LocalizationManager>.Instance.CurrentCulture.String ?? Default; }
            catch { return Default; }
        }
    }

    public static void Install()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(Lang).Assembly.Location);
            var file = dir != null ? Path.Combine(dir, "SorterUnlock.lang.json") : null;
            if (file != null && File.Exists(file))
            {
                _overrides = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file));
                Log.Write($"LANG: overrides loaded from {file}");
            }
        }
        catch (Exception ex) { Log.Error("LANG overrides", ex); }
    }

    /// <summary>Subscribe lazily: the localization manager exists only once the game is up.</summary>
    public static void EnsureHooked()
    {
        if (_hooked) return;
        try
        {
            Singleton<LocalizationManager>.Instance.OnCultureLoaded += () =>
            {
                Log.Write($"LANG: game language -> {Culture}");
                Changed?.Invoke();
            };
            _hooked = true;
        }
        catch (Exception ex) { Log.Error("LANG hook", ex); }
    }

    public static string T(string key)
    {
        var c = Culture;
        if (_overrides != null)
        {
            if (_overrides.TryGetValue(c, out var o) && o.TryGetValue(key, out var ov)) return ov;
        }
        if (Texts.TryGetValue(c, out var d) && d.TryGetValue(key, out var v)) return v;
        // same language, other region (e.g. "ru" / "cs-CZ")
        var lang = c.Split('-')[0];
        foreach (var kv in Texts)
            if (kv.Key.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase) && kv.Value.TryGetValue(key, out var v2)) return v2;
        return Texts[Default].TryGetValue(key, out var e) ? e : key;
    }

    public static string F(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>Official game text if the key exists in the game, otherwise our own.</summary>
    public static string Game(string gameKey, string ownKey)
    {
        try
        {
            var s = LocKey.FromString(gameKey).EvaluateText();
            if (!string.IsNullOrWhiteSpace(s) && s != gameKey) return s;
        }
        catch { }
        return T(ownKey);
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Texts = new()
    {
        ["en-US"] = new()
        {
            ["title"] = "Item filter",
            ["mode_label"] = "Filter mode",
            ["mode_pass"] = "Allow selected",
            ["mode_block"] = "Block selected",
            ["hint_empty"] = "Nothing selected — the filter is off, everything passes.",
            ["hint_pass"] = "Only this passes the sorter: {0}.",
            ["hint_block"] = "Everything passes the sorter except: {0}.",
            ["more"] = "{0} and {1} more",
            ["warn_drain_empty"] = "ℹ \"Drain all\" is on and the filter is empty: the pump moves everything.",
            ["warn_opposite"] = "ℹ Opposite flow is on: everything passes back (output to input), unfiltered.",
            ["multi"] = "Sorters selected: {0}, changes apply to all of them.",
            ["no_sel"] = "No sorter selected. Select a sorter in the list on the left.",
            ["no_server"] = "Sorter not found on the server. This panel works only in single player or as the host.",
            ["clear_all"] = "Clear all",
            ["collapse"] = "Collapse groups",
            ["search"] = "Search item…",
            ["all"] = "all",
            ["grp_ore"] = "Ores", ["grp_ingots"] = "Ingots", ["grp_components"] = "Components", ["grp_tools"] = "Tools",
            ["grp_consumables"] = "Consumables", ["grp_blueprints"] = "Blueprints", ["grp_datapads"] = "Datapads",
            ["grp_quest"] = "Quest items", ["grp_other"] = "Other",
            ["maint_title"] = "Transfer mode",
            ["maint_desc"] = "The sorter stops pumping for a while and becomes an open pipe both ways (no filtering), so another pump can move items through it. Its settings are not changed and apply again when the countdown ends.",
            ["maint_btn"] = "⇄ Transfer",
            ["maint_for"] = "for",
            ["maint_auto"] = "Finish when the pumps are done",
            ["maint_stop"] = "Restore now",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Transfer: {0} until restore",
            ["maint_status_auto"] = " (or earlier, when the pumps are done)",
            ["build_fail"] = "SorterUnlock: the panel failed to build, see the log",
        },
        ["ru-RU"] = new()
        {
            ["title"] = "Фильтр предметов",
            ["mode_label"] = "Режим фильтра",
            ["mode_pass"] = "Пропускать отмеченные",
            ["mode_block"] = "Блокировать отмеченные",
            ["hint_empty"] = "Ничего не отмечено — фильтр не действует, проходит всё.",
            ["hint_pass"] = "Через сортер пройдёт только: {0}.",
            ["hint_block"] = "Через сортер пройдёт всё, кроме: {0}.",
            ["more"] = "{0} и ещё {1}",
            ["warn_drain_empty"] = "ℹ «Слить всё» включено, а фильтр пуст: насос качает всё подряд.",
            ["warn_opposite"] = "ℹ Встречный поток включён: обратно (с выхода на вход) проходит всё, без фильтра.",
            ["multi"] = "Сортеров выбрано: {0}, изменения применятся ко всем.",
            ["no_sel"] = "Не вижу выбранный сортер. Выбери сортер в списке слева.",
            ["no_server"] = "Сортер не найден на сервере. Панель работает только в одиночной игре или на хосте.",
            ["clear_all"] = "Снять все",
            ["collapse"] = "Свернуть группы",
            ["search"] = "Поиск предмета…",
            ["all"] = "все",
            ["grp_ore"] = "Руда", ["grp_ingots"] = "Слитки", ["grp_components"] = "Компоненты", ["grp_tools"] = "Инструменты",
            ["grp_consumables"] = "Расходники", ["grp_blueprints"] = "Чертежи", ["grp_datapads"] = "Датапады",
            ["grp_quest"] = "Квестовые предметы", ["grp_other"] = "Прочее",
            ["maint_title"] = "Перекачка",
            ["maint_desc"] = "Сортер временно перестаёт качать и становится открытой трубой в обе стороны (фильтр не действует), чтобы другой насос мог перекачать предметы через него. Настройки сортера не меняются и после отсчёта снова действуют.",
            ["maint_btn"] = "⇄ Перекачка",
            ["maint_for"] = "на",
            ["maint_auto"] = "Завершить, когда насосы закончат",
            ["maint_stop"] = "Вернуть сейчас",
            ["maint_more"] = "+30 с",
            ["maint_status"] = "⏸ Перекачка: {0} до возврата",
            ["maint_status_auto"] = " (или раньше, когда насосы закончат)",
            ["build_fail"] = "SorterUnlock: панель не создалась, см. лог",
        },
        ["uk-UA"] = new()
        {
            ["title"] = "Фільтр предметів",
            ["mode_label"] = "Режим фільтра",
            ["mode_pass"] = "Пропускати позначені",
            ["mode_block"] = "Блокувати позначені",
            ["hint_empty"] = "Нічого не позначено — фільтр не діє, проходить усе.",
            ["hint_pass"] = "Через сортер пройде лише: {0}.",
            ["hint_block"] = "Через сортер пройде все, крім: {0}.",
            ["more"] = "{0} і ще {1}",
            ["warn_drain_empty"] = "ℹ «Злити все» увімкнено, а фільтр порожній: насос качає все підряд.",
            ["warn_opposite"] = "ℹ Зустрічний потік увімкнено: назад (з виходу на вхід) проходить усе, без фільтра.",
            ["multi"] = "Вибрано сортерів: {0}, зміни застосуються до всіх.",
            ["no_sel"] = "Сортер не вибрано. Вибери сортер у списку ліворуч.",
            ["no_server"] = "Сортер не знайдено на сервері. Панель працює лише в одиночній грі або на хості.",
            ["clear_all"] = "Зняти всі",
            ["collapse"] = "Згорнути групи",
            ["search"] = "Пошук предмета…",
            ["all"] = "усі",
            ["grp_ore"] = "Руда", ["grp_ingots"] = "Злитки", ["grp_components"] = "Компоненти", ["grp_tools"] = "Інструменти",
            ["grp_consumables"] = "Витратні матеріали", ["grp_blueprints"] = "Креслення", ["grp_datapads"] = "Датапади",
            ["grp_quest"] = "Квестові предмети", ["grp_other"] = "Інше",
            ["maint_title"] = "Перекачування",
            ["maint_desc"] = "Сортер тимчасово перестає качати й стає відкритою трубою в обидва боки (фільтр не діє), щоб інший насос міг перекачати предмети через нього. Налаштування сортера не змінюються й після відліку знову діють.",
            ["maint_btn"] = "⇄ Перекачування",
            ["maint_for"] = "на",
            ["maint_auto"] = "Завершити, коли насоси закінчать",
            ["maint_stop"] = "Повернути зараз",
            ["maint_more"] = "+30 с",
            ["maint_status"] = "⏸ Перекачування: {0} до повернення",
            ["maint_status_auto"] = " (або раніше, коли насоси закінчать)",
            ["build_fail"] = "SorterUnlock: панель не створилася, див. журнал",
        },
        ["de-DE"] = new()
        {
            ["title"] = "Gegenstandsfilter",
            ["mode_label"] = "Filtermodus",
            ["mode_pass"] = "Ausgewählte durchlassen",
            ["mode_block"] = "Ausgewählte blockieren",
            ["hint_empty"] = "Nichts ausgewählt – der Filter ist aus, alles kommt durch.",
            ["hint_pass"] = "Nur das kommt durch den Sortierer: {0}.",
            ["hint_block"] = "Alles kommt durch den Sortierer außer: {0}.",
            ["more"] = "{0} und {1} weitere",
            ["warn_drain_empty"] = "ℹ „Alles abpumpen“ ist an und der Filter ist leer: die Pumpe bewegt alles.",
            ["warn_opposite"] = "ℹ Gegenfluss ist an: alles geht ungefiltert zurück (vom Ausgang zum Eingang).",
            ["multi"] = "Ausgewählte Sortierer: {0}, Änderungen gelten für alle.",
            ["no_sel"] = "Kein Sortierer ausgewählt. Wähle links einen Sortierer aus.",
            ["no_server"] = "Sortierer auf dem Server nicht gefunden. Dieses Feld funktioniert nur im Einzelspieler oder als Host.",
            ["clear_all"] = "Alle abwählen",
            ["collapse"] = "Gruppen einklappen",
            ["search"] = "Gegenstand suchen…",
            ["all"] = "alle",
            ["grp_ore"] = "Erze", ["grp_ingots"] = "Barren", ["grp_components"] = "Komponenten", ["grp_tools"] = "Werkzeuge",
            ["grp_consumables"] = "Verbrauchsgüter", ["grp_blueprints"] = "Baupläne", ["grp_datapads"] = "Datapads",
            ["grp_quest"] = "Questgegenstände", ["grp_other"] = "Sonstiges",
            ["maint_title"] = "Umpumpen",
            ["maint_desc"] = "Der Sortierer pumpt eine Weile nicht und wird zu einem offenen Rohr in beide Richtungen (kein Filter), damit eine andere Pumpe Gegenstände hindurch befördern kann. Seine Einstellungen bleiben unverändert und gelten nach dem Countdown wieder.",
            ["maint_btn"] = "⇄ Umpumpen",
            ["maint_for"] = "für",
            ["maint_auto"] = "Beenden, wenn die Pumpen fertig sind",
            ["maint_stop"] = "Jetzt zurücksetzen",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Umpumpen: noch {0} bis zur Rückkehr",
            ["maint_status_auto"] = " (oder früher, wenn die Pumpen fertig sind)",
            ["build_fail"] = "SorterUnlock: Das Feld konnte nicht erstellt werden, siehe Log",
        },
        ["fr-FR"] = new()
        {
            ["title"] = "Filtre d'objets",
            ["mode_label"] = "Mode du filtre",
            ["mode_pass"] = "Laisser passer la sélection",
            ["mode_block"] = "Bloquer la sélection",
            ["hint_empty"] = "Rien n'est sélectionné : le filtre est inactif, tout passe.",
            ["hint_pass"] = "Seul ceci passe par le trieur : {0}.",
            ["hint_block"] = "Tout passe par le trieur sauf : {0}.",
            ["more"] = "{0} et {1} de plus",
            ["warn_drain_empty"] = "ℹ « Tout vider » est activé et le filtre est vide : la pompe déplace tout.",
            ["warn_opposite"] = "ℹ Le flux inverse est activé : tout repasse en sens inverse (de la sortie à l'entrée), sans filtre.",
            ["multi"] = "Trieurs sélectionnés : {0}, les changements s'appliquent à tous.",
            ["no_sel"] = "Aucun trieur sélectionné. Sélectionne un trieur dans la liste à gauche.",
            ["no_server"] = "Trieur introuvable sur le serveur. Ce panneau ne fonctionne qu'en solo ou en tant qu'hôte.",
            ["clear_all"] = "Tout décocher",
            ["collapse"] = "Replier les groupes",
            ["search"] = "Rechercher un objet…",
            ["all"] = "tous",
            ["grp_ore"] = "Minerais", ["grp_ingots"] = "Lingots", ["grp_components"] = "Composants", ["grp_tools"] = "Outils",
            ["grp_consumables"] = "Consommables", ["grp_blueprints"] = "Plans", ["grp_datapads"] = "Datapads",
            ["grp_quest"] = "Objets de quête", ["grp_other"] = "Autres",
            ["maint_title"] = "Transfert",
            ["maint_desc"] = "Le trieur arrête de pomper un moment et devient un tuyau ouvert dans les deux sens (sans filtre), pour qu'une autre pompe puisse faire passer des objets. Ses réglages ne changent pas et s'appliquent de nouveau à la fin du compte à rebours.",
            ["maint_btn"] = "⇄ Transfert",
            ["maint_for"] = "pendant",
            ["maint_auto"] = "Terminer quand les pompes ont fini",
            ["maint_stop"] = "Rétablir maintenant",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Transfert : {0} avant le retour",
            ["maint_status_auto"] = " (ou plus tôt, quand les pompes ont fini)",
            ["build_fail"] = "SorterUnlock : le panneau n'a pas pu être créé, voir le journal",
        },
        ["es-ES"] = new()
        {
            ["title"] = "Filtro de objetos",
            ["mode_label"] = "Modo del filtro",
            ["mode_pass"] = "Dejar pasar lo marcado",
            ["mode_block"] = "Bloquear lo marcado",
            ["hint_empty"] = "No hay nada marcado: el filtro no actúa, pasa todo.",
            ["hint_pass"] = "Por el clasificador solo pasa: {0}.",
            ["hint_block"] = "Por el clasificador pasa todo excepto: {0}.",
            ["more"] = "{0} y {1} más",
            ["warn_drain_empty"] = "ℹ «Vaciar todo» está activado y el filtro está vacío: la bomba mueve todo.",
            ["warn_opposite"] = "ℹ El flujo inverso está activado: todo vuelve (de la salida a la entrada) sin filtrar.",
            ["multi"] = "Clasificadores seleccionados: {0}, los cambios se aplican a todos.",
            ["no_sel"] = "No hay ningún clasificador seleccionado. Selecciona uno en la lista de la izquierda.",
            ["no_server"] = "No se encontró el clasificador en el servidor. Este panel solo funciona en un jugador o como anfitrión.",
            ["clear_all"] = "Desmarcar todo",
            ["collapse"] = "Contraer grupos",
            ["search"] = "Buscar objeto…",
            ["all"] = "todos",
            ["grp_ore"] = "Menas", ["grp_ingots"] = "Lingotes", ["grp_components"] = "Componentes", ["grp_tools"] = "Herramientas",
            ["grp_consumables"] = "Consumibles", ["grp_blueprints"] = "Planos", ["grp_datapads"] = "Datapads",
            ["grp_quest"] = "Objetos de misión", ["grp_other"] = "Otros",
            ["maint_title"] = "Trasvase",
            ["maint_desc"] = "El clasificador deja de bombear un tiempo y se convierte en una tubería abierta en ambos sentidos (sin filtro), para que otra bomba pueda pasar objetos a través de él. Su configuración no cambia y vuelve a aplicarse al terminar la cuenta atrás.",
            ["maint_btn"] = "⇄ Trasvase",
            ["maint_for"] = "durante",
            ["maint_auto"] = "Terminar cuando las bombas acaben",
            ["maint_stop"] = "Restaurar ahora",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Trasvase: {0} para restaurar",
            ["maint_status_auto"] = " (o antes, cuando las bombas acaben)",
            ["build_fail"] = "SorterUnlock: no se pudo crear el panel, mira el registro",
        },
        ["it-IT"] = new()
        {
            ["title"] = "Filtro oggetti",
            ["mode_label"] = "Modalità filtro",
            ["mode_pass"] = "Lascia passare i selezionati",
            ["mode_block"] = "Blocca i selezionati",
            ["hint_empty"] = "Nulla di selezionato: il filtro non agisce, passa tutto.",
            ["hint_pass"] = "Attraverso lo smistatore passa solo: {0}.",
            ["hint_block"] = "Attraverso lo smistatore passa tutto tranne: {0}.",
            ["more"] = "{0} e altri {1}",
            ["warn_drain_empty"] = "ℹ «Svuota tutto» è attivo e il filtro è vuoto: la pompa sposta tutto.",
            ["warn_opposite"] = "ℹ Il flusso inverso è attivo: tutto torna indietro (dall'uscita all'ingresso) senza filtro.",
            ["multi"] = "Smistatori selezionati: {0}, le modifiche valgono per tutti.",
            ["no_sel"] = "Nessuno smistatore selezionato. Selezionane uno nell'elenco a sinistra.",
            ["no_server"] = "Smistatore non trovato sul server. Questo pannello funziona solo in giocatore singolo o come host.",
            ["clear_all"] = "Deseleziona tutto",
            ["collapse"] = "Comprimi gruppi",
            ["search"] = "Cerca oggetto…",
            ["all"] = "tutti",
            ["grp_ore"] = "Minerali", ["grp_ingots"] = "Lingotti", ["grp_components"] = "Componenti", ["grp_tools"] = "Strumenti",
            ["grp_consumables"] = "Consumabili", ["grp_blueprints"] = "Progetti", ["grp_datapads"] = "Datapad",
            ["grp_quest"] = "Oggetti missione", ["grp_other"] = "Altro",
            ["maint_title"] = "Travaso",
            ["maint_desc"] = "Lo smistatore smette di pompare per un po' e diventa un tubo aperto in entrambe le direzioni (senza filtro), così un'altra pompa può far passare oggetti attraverso di esso. Le sue impostazioni non cambiano e tornano attive al termine del conto alla rovescia.",
            ["maint_btn"] = "⇄ Travaso",
            ["maint_for"] = "per",
            ["maint_auto"] = "Termina quando le pompe hanno finito",
            ["maint_stop"] = "Ripristina ora",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Travaso: {0} al ripristino",
            ["maint_status_auto"] = " (o prima, quando le pompe hanno finito)",
            ["build_fail"] = "SorterUnlock: impossibile creare il pannello, vedi il log",
        },
        ["pl-PL"] = new()
        {
            ["title"] = "Filtr przedmiotów",
            ["mode_label"] = "Tryb filtra",
            ["mode_pass"] = "Przepuszczaj zaznaczone",
            ["mode_block"] = "Blokuj zaznaczone",
            ["hint_empty"] = "Nic nie zaznaczono — filtr nie działa, przechodzi wszystko.",
            ["hint_pass"] = "Przez sorter przejdzie tylko: {0}.",
            ["hint_block"] = "Przez sorter przejdzie wszystko oprócz: {0}.",
            ["more"] = "{0} i jeszcze {1}",
            ["warn_drain_empty"] = "ℹ „Opróżnij wszystko” jest włączone, a filtr jest pusty: pompa przenosi wszystko.",
            ["warn_opposite"] = "ℹ Przepływ wsteczny jest włączony: wszystko wraca (z wyjścia na wejście) bez filtra.",
            ["multi"] = "Wybrane sortery: {0}, zmiany dotyczą wszystkich.",
            ["no_sel"] = "Nie wybrano sortera. Wybierz sorter z listy po lewej.",
            ["no_server"] = "Nie znaleziono sortera na serwerze. Ten panel działa tylko w grze jednoosobowej lub jako host.",
            ["clear_all"] = "Odznacz wszystko",
            ["collapse"] = "Zwiń grupy",
            ["search"] = "Szukaj przedmiotu…",
            ["all"] = "wszystkie",
            ["grp_ore"] = "Rudy", ["grp_ingots"] = "Sztabki", ["grp_components"] = "Komponenty", ["grp_tools"] = "Narzędzia",
            ["grp_consumables"] = "Materiały eksploatacyjne", ["grp_blueprints"] = "Schematy", ["grp_datapads"] = "Datapady",
            ["grp_quest"] = "Przedmioty fabularne", ["grp_other"] = "Inne",
            ["maint_title"] = "Przepompowanie",
            ["maint_desc"] = "Sorter na chwilę przestaje pompować i staje się otwartą rurą w obie strony (bez filtra), aby inna pompa mogła przez niego przenieść przedmioty. Jego ustawienia się nie zmieniają i znów działają po odliczaniu.",
            ["maint_btn"] = "⇄ Przepompuj",
            ["maint_for"] = "na",
            ["maint_auto"] = "Zakończ, gdy pompy skończą",
            ["maint_stop"] = "Przywróć teraz",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Przepompowanie: {0} do przywrócenia",
            ["maint_status_auto"] = " (lub wcześniej, gdy pompy skończą)",
            ["build_fail"] = "SorterUnlock: nie udało się utworzyć panelu, zobacz log",
        },
        ["cz-CZ"] = new()
        {
            ["title"] = "Filtr předmětů",
            ["mode_label"] = "Režim filtru",
            ["mode_pass"] = "Propouštět označené",
            ["mode_block"] = "Blokovat označené",
            ["hint_empty"] = "Nic není označeno — filtr nepůsobí, projde vše.",
            ["hint_pass"] = "Třídičem projde jen: {0}.",
            ["hint_block"] = "Třídičem projde vše kromě: {0}.",
            ["more"] = "{0} a dalších {1}",
            ["warn_drain_empty"] = "ℹ „Vyprázdnit vše“ je zapnuto a filtr je prázdný: pumpa přesouvá všechno.",
            ["warn_opposite"] = "ℹ Zpětný tok je zapnutý: vše prochází zpět (z výstupu na vstup) bez filtru.",
            ["multi"] = "Vybrané třídiče: {0}, změny platí pro všechny.",
            ["no_sel"] = "Není vybrán žádný třídič. Vyber třídič v seznamu vlevo.",
            ["no_server"] = "Třídič nebyl na serveru nalezen. Tento panel funguje jen v singleplayeru nebo jako hostitel.",
            ["clear_all"] = "Odznačit vše",
            ["collapse"] = "Sbalit skupiny",
            ["search"] = "Hledat předmět…",
            ["all"] = "vše",
            ["grp_ore"] = "Rudy", ["grp_ingots"] = "Ingoty", ["grp_components"] = "Komponenty", ["grp_tools"] = "Nástroje",
            ["grp_consumables"] = "Spotřební předměty", ["grp_blueprints"] = "Plány", ["grp_datapads"] = "Datapady",
            ["grp_quest"] = "Questové předměty", ["grp_other"] = "Ostatní",
            ["maint_title"] = "Přečerpání",
            ["maint_desc"] = "Třídič na chvíli přestane čerpat a stane se otevřenou trubkou oběma směry (bez filtru), aby jím jiná pumpa mohla přesunout předměty. Jeho nastavení se nemění a po odpočtu opět platí.",
            ["maint_btn"] = "⇄ Přečerpat",
            ["maint_for"] = "na",
            ["maint_auto"] = "Ukončit, až pumpy skončí",
            ["maint_stop"] = "Obnovit teď",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Přečerpání: {0} do obnovení",
            ["maint_status_auto"] = " (nebo dříve, až pumpy skončí)",
            ["build_fail"] = "SorterUnlock: panel se nepodařilo vytvořit, viz log",
        },
        ["sk-SK"] = new()
        {
            ["title"] = "Filter predmetov",
            ["mode_label"] = "Režim filtra",
            ["mode_pass"] = "Prepúšťať označené",
            ["mode_block"] = "Blokovať označené",
            ["hint_empty"] = "Nič nie je označené — filter nepôsobí, prejde všetko.",
            ["hint_pass"] = "Triedičom prejde iba: {0}.",
            ["hint_block"] = "Triedičom prejde všetko okrem: {0}.",
            ["more"] = "{0} a ďalších {1}",
            ["warn_drain_empty"] = "ℹ „Vyprázdniť všetko“ je zapnuté a filter je prázdny: pumpa presúva všetko.",
            ["warn_opposite"] = "ℹ Spätný tok je zapnutý: všetko prechádza späť (z výstupu na vstup) bez filtra.",
            ["multi"] = "Vybrané triediče: {0}, zmeny platia pre všetky.",
            ["no_sel"] = "Nie je vybraný žiadny triedič. Vyber triedič v zozname vľavo.",
            ["no_server"] = "Triedič sa na serveri nenašiel. Tento panel funguje iba v hre pre jedného hráča alebo ako hostiteľ.",
            ["clear_all"] = "Odznačiť všetko",
            ["collapse"] = "Zbaliť skupiny",
            ["search"] = "Hľadať predmet…",
            ["all"] = "všetky",
            ["grp_ore"] = "Rudy", ["grp_ingots"] = "Ingoty", ["grp_components"] = "Komponenty", ["grp_tools"] = "Nástroje",
            ["grp_consumables"] = "Spotrebné predmety", ["grp_blueprints"] = "Plány", ["grp_datapads"] = "Datapady",
            ["grp_quest"] = "Questové predmety", ["grp_other"] = "Ostatné",
            ["maint_title"] = "Prečerpanie",
            ["maint_desc"] = "Triedič na chvíľu prestane čerpať a stane sa otvorenou rúrou oboma smermi (bez filtra), aby ním iná pumpa mohla presunúť predmety. Jeho nastavenia sa nemenia a po odpočte opäť platia.",
            ["maint_btn"] = "⇄ Prečerpať",
            ["maint_for"] = "na",
            ["maint_auto"] = "Ukončiť, keď pumpy skončia",
            ["maint_stop"] = "Obnoviť teraz",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Prečerpanie: {0} do obnovenia",
            ["maint_status_auto"] = " (alebo skôr, keď pumpy skončia)",
            ["build_fail"] = "SorterUnlock: panel sa nepodarilo vytvoriť, pozri log",
        },
        ["pt-BR"] = new()
        {
            ["title"] = "Filtro de itens",
            ["mode_label"] = "Modo do filtro",
            ["mode_pass"] = "Deixar passar os marcados",
            ["mode_block"] = "Bloquear os marcados",
            ["hint_empty"] = "Nada marcado — o filtro não atua, tudo passa.",
            ["hint_pass"] = "Pelo separador passa apenas: {0}.",
            ["hint_block"] = "Pelo separador passa tudo, exceto: {0}.",
            ["more"] = "{0} e mais {1}",
            ["warn_drain_empty"] = "ℹ \"Esvaziar tudo\" está ligado e o filtro está vazio: a bomba move tudo.",
            ["warn_opposite"] = "ℹ O fluxo reverso está ligado: tudo volta (da saída para a entrada) sem filtro.",
            ["multi"] = "Separadores selecionados: {0}, as mudanças valem para todos.",
            ["no_sel"] = "Nenhum separador selecionado. Selecione um separador na lista à esquerda.",
            ["no_server"] = "Separador não encontrado no servidor. Este painel só funciona no modo um jogador ou como anfitrião.",
            ["clear_all"] = "Desmarcar tudo",
            ["collapse"] = "Recolher grupos",
            ["search"] = "Buscar item…",
            ["all"] = "todos",
            ["grp_ore"] = "Minérios", ["grp_ingots"] = "Lingotes", ["grp_components"] = "Componentes", ["grp_tools"] = "Ferramentas",
            ["grp_consumables"] = "Consumíveis", ["grp_blueprints"] = "Projetos", ["grp_datapads"] = "Datapads",
            ["grp_quest"] = "Itens de missão", ["grp_other"] = "Outros",
            ["maint_title"] = "Transferência",
            ["maint_desc"] = "O separador para de bombear por um tempo e vira um tubo aberto nos dois sentidos (sem filtro), para que outra bomba possa passar itens por ele. As configurações dele não mudam e voltam a valer quando a contagem termina.",
            ["maint_btn"] = "⇄ Transferir",
            ["maint_for"] = "por",
            ["maint_auto"] = "Encerrar quando as bombas terminarem",
            ["maint_stop"] = "Restaurar agora",
            ["maint_more"] = "+30 s",
            ["maint_status"] = "⏸ Transferência: {0} para restaurar",
            ["maint_status_auto"] = " (ou antes, quando as bombas terminarem)",
            ["build_fail"] = "SorterUnlock: não foi possível criar o painel, veja o log",
        },
        ["tr-TR"] = new()
        {
            ["title"] = "Eşya filtresi",
            ["mode_label"] = "Filtre modu",
            ["mode_pass"] = "Seçilenleri geçir",
            ["mode_block"] = "Seçilenleri engelle",
            ["hint_empty"] = "Hiçbir şey seçilmedi — filtre devre dışı, her şey geçer.",
            ["hint_pass"] = "Ayırıcıdan yalnızca şunlar geçer: {0}.",
            ["hint_block"] = "Ayırıcıdan şunlar dışında her şey geçer: {0}.",
            ["more"] = "{0} ve {1} tane daha",
            ["warn_drain_empty"] = "ℹ \"Tümünü boşalt\" açık ve filtre boş: pompa her şeyi taşır.",
            ["warn_opposite"] = "ℹ Ters akış açık: her şey filtresiz olarak geri (çıkıştan girişe) geçer.",
            ["multi"] = "Seçili ayırıcılar: {0}, değişiklikler hepsine uygulanır.",
            ["no_sel"] = "Ayırıcı seçilmedi. Soldaki listeden bir ayırıcı seç.",
            ["no_server"] = "Ayırıcı sunucuda bulunamadı. Bu panel yalnızca tek oyunculu modda veya sunucu sahibiyken çalışır.",
            ["clear_all"] = "Tümünü kaldır",
            ["collapse"] = "Grupları daralt",
            ["search"] = "Eşya ara…",
            ["all"] = "tümü",
            ["grp_ore"] = "Cevherler", ["grp_ingots"] = "Külçeler", ["grp_components"] = "Bileşenler", ["grp_tools"] = "Aletler",
            ["grp_consumables"] = "Sarf malzemeleri", ["grp_blueprints"] = "Planlar", ["grp_datapads"] = "Veri pedleri",
            ["grp_quest"] = "Görev eşyaları", ["grp_other"] = "Diğer",
            ["maint_title"] = "Aktarım",
            ["maint_desc"] = "Ayırıcı bir süre pompalamayı bırakır ve iki yönde de açık bir boruya dönüşür (filtresiz); böylece başka bir pompa eşyaları içinden geçirebilir. Ayarları değişmez ve geri sayım bitince yeniden geçerli olur.",
            ["maint_btn"] = "⇄ Aktar",
            ["maint_for"] = "süre",
            ["maint_auto"] = "Pompalar bitince sonlandır",
            ["maint_stop"] = "Şimdi geri al",
            ["maint_more"] = "+30 sn",
            ["maint_status"] = "⏸ Aktarım: geri dönüşe {0}",
            ["maint_status_auto"] = " (veya pompalar bitince daha erken)",
            ["build_fail"] = "SorterUnlock: panel oluşturulamadı, günlüğe bak",
        },
        ["zh-CN"] = new()
        {
            ["title"] = "物品过滤器",
            ["mode_label"] = "过滤模式",
            ["mode_pass"] = "放行已选物品",
            ["mode_block"] = "阻止已选物品",
            ["hint_empty"] = "未选择任何物品——过滤器不起作用，全部放行。",
            ["hint_pass"] = "只有这些能通过分拣器：{0}。",
            ["hint_block"] = "除以下物品外全部通过分拣器：{0}。",
            ["more"] = "{0} 等另外 {1} 项",
            ["warn_drain_empty"] = "ℹ 已开启“全部抽空”且过滤器为空：泵会转移所有物品。",
            ["warn_opposite"] = "ℹ 已开启反向流动：所有物品不经过滤反向通过（从出口到入口）。",
            ["multi"] = "已选分拣器：{0} 个，更改将应用于全部。",
            ["no_sel"] = "未选择分拣器。请在左侧列表中选择一个分拣器。",
            ["no_server"] = "服务器上找不到该分拣器。此面板仅在单人游戏或作为主机时可用。",
            ["clear_all"] = "全部取消",
            ["collapse"] = "折叠分组",
            ["search"] = "搜索物品…",
            ["all"] = "全部",
            ["grp_ore"] = "矿石", ["grp_ingots"] = "锭", ["grp_components"] = "组件", ["grp_tools"] = "工具",
            ["grp_consumables"] = "消耗品", ["grp_blueprints"] = "蓝图", ["grp_datapads"] = "数据板",
            ["grp_quest"] = "任务物品", ["grp_other"] = "其他",
            ["maint_title"] = "转运",
            ["maint_desc"] = "分拣器暂时停止抽送，变成双向畅通的管道（不过滤），以便其他泵能通过它转移物品。分拣器的设置不会改变，倒计时结束后重新生效。",
            ["maint_btn"] = "⇄ 转运",
            ["maint_for"] = "时长",
            ["maint_auto"] = "泵完成后自动结束",
            ["maint_stop"] = "立即恢复",
            ["maint_more"] = "+30 秒",
            ["maint_status"] = "⏸ 转运中：{0} 后恢复",
            ["maint_status_auto"] = "（泵完成后会提前恢复）",
            ["build_fail"] = "SorterUnlock：面板创建失败，请查看日志",
        },
    };
}
