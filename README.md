# RimMult — мультиплеер для RimWorld 1.6

**[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3813029532)** ·
**[Releases](https://github.com/Vanket/RimMult/releases)** ·
[English below](#english)

От 2 до 10 игроков через Steam. Без lockstep: **каждый компьютер считает только свою игру** и отправляет другим
изменения. Поэтому моды не вызывают рассинхронов, а слабый ПК не тормозит из-за чужих колоний.

> **Ранняя версия.** Подключение, общий мир, посылки, торговля и кооп проверены в игре; рейды, помощь союзнику,
> летопись и часть кооп-функций — написаны и проходят проверку. Что именно проверено — в
> [`docs/HANDOFF.md`](docs/HANDOFF.md).

## Что умеет

### Раздельные колонии
У каждого своя колония на общей планете. Общий календарь, скорость — голосованием (паузу может поставить любой).

- **Посылки** капсулами или караваном — даже тому, кто сейчас не в игре: получит, когда зайдёт.
- **Переселение** колонистов и животных к другу.
- **Обмен**: «Предложить обмен» на колонии игрока или приход караваном. Оба принимают — товары прилетают капсулами.
- **Дипломатия**: нейтралитет, союз, война (PvP хост может выключить).
- **Рейды** караваном или капсулами: под управлением ИИ или **лично** — вы загружаете игру защитника и командуете
  своими бойцами (отступить, похитить, украсть, вернуться домой), пока он обороняется. Выжившие возвращаются
  караваном с добычей и пленными.
- **Помощь союзнику лично** — в его квестах или против рейда.
- **Общий мир NPC**: разгромленное поселение исчезает у всех.
- **Летопись мира**: основанные колонии, войны, рейды и их итоги, посылки; таблица игроков — богатство, колонисты,
  дела. Хранится в сейве хоста (игра через Steam) или на выделенном сервере.

### Кооп на одной карте
Все вместе играют колонию хоста, считает её ПК хоста.

- Гости видят колонию вживую: плавное движение, выстрелы, прицелы, прогресс работ.
- Любые приказы: разметка, стройка, призыв, приказы правой кнопкой (очередь с Shift), атака по цели и способности,
  приоритеты, исследования, квесты.
- Счета, склады, растения, владельцы кроватей, расписания, политики, разрешённые области, караваны на глобусе.
- Курсоры друзей и то, что они выделили, — их цветом.
- Данные модов тоже доходят до гостей; расхождения регулярно проверяются и чинятся; «Загрузить заново», если что-то
  выглядит не так.

## Установка

**Проще всего — подписаться в [Мастерской Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3813029532)**
(Steam сам предложит [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)).

У всех игроков должна быть **одна и та же версия** RimMult: сервер сравнивает моды вплоть до DLL и не пустит с другой.
Если Steam не обновил мод сам, перезапустите клиент Steam.

<details>
<summary>Без Мастерской: свежая сборка из <code>main</code></summary>

Сборка из `main` лежит в релизе [latest](https://github.com/Vanket/RimMult/releases/tag/latest). Установить или
обновить одной командой в PowerShell (путь к `Mods` замените на свой; подписку в Мастерской на это время отключите —
иначе будет два мода с одним packageId):

```powershell
$mods = "D:\SteamLibrary\steamapps\common\RimWorld\Mods"; $zip = "$env:TEMP\RimMult.zip"; [Net.ServicePointManager]::SecurityProtocol = 'Tls12'; Invoke-WebRequest "https://github.com/Vanket/RimMult/releases/download/latest/RimMult.zip" -OutFile $zip -UseBasicParsing; Remove-Item "$mods\RimMult" -Recurse -Force -ErrorAction SilentlyContinue; Expand-Archive $zip -DestinationPath $mods -Force
```
</details>

## Как играть

1. **Хост:** главное меню (или меню паузы в загруженной игре) → **«Мультиплеер»** → задать название, пароль, режим →
   «Создать». Можно «Загрузить сейв и создать игру».
2. **Друзья** заходят из списка «Друзья в Steam…» в том же окне или из списка друзей Steam («Присоединиться к игре»).
   Вход по IP — по желанию (галочка «Разрешить вход по адресу», UDP-порт).
3. **Раздельные колонии:** «Создать колонию» (планета общая и заблокирована) или «Загрузить свою колонию».
   **Кооп:** «Войти в колонию хоста».
4. Чат — клавиша **`\`**. Полоска сверху показывает игроков, скорость и ваш голос; клик по ней открывает окно
   мультиплеера (там же **«Летопись мира»** и **«Отчёт об ошибке»**).

## Моды

- Моды у всех одинаковые и в одном порядке. Кнопка **«Моды»** рядом с другом в окне мультиплеера сравнит ваши моды с
  его **до входа**; то же покажет и неудачный вход. Кнопка **«Сделать всё как у хоста»** подпишет на недостающие моды,
  заставит Steam скачать свежие версии отличающихся (включая сам RimMult), включит и выключит нужные, расставит в
  порядке хоста, перезапустит игру и сама подключится — один перезапуск.
- **Какая у меня версия?** В заголовке окна мультиплеера: «RimMult 0.5.0 (ca6e6a1)» — версия и сборка. На экране
  различий видно и сборку хоста. «v15/v16» — это версия протокола, она одна на много сборок.
- Чисто визуальные моды и моды интерфейса (превью карты, камера, HUD, переводы) можно отметить **«клиентскими»** в
  настройках RimMult — тогда они могут отличаться. Map Preview, Camera+, RimHUD и Dubs Mint Menus/Minimap отмечены сразу.
- Не работает вместе с модом **Multiplayer** (Zetrith). Моды, меняющие скорость игры (Smart Speed), спорят с общим
  голосованием. Разбор 300+ популярных модов — [`docs/MODS.md`](docs/MODS.md). Авторам модов — [`docs/API.md`](docs/API.md).

## Выделенный сервер

Для раздельных колоний можно держать мир на сервере (Linux, Windows, Docker), а не у хоста:

```sh
docker compose up -d         # мир, конфиг и бэкапы в ./server-data
docker attach rimmult        # консоль: help, players, kick, ban, say, pvp…
```

Или готовая сборка из [Releases](https://github.com/Vanket/RimMult/releases). Команды, баны, админы, бэкапы —
[`docs/SERVER.md`](docs/SERVER.md). Админские команды работают и в чате игры у хоста: `/kick`, `/ban`, `/pvp off`…

## Нашли ошибку?

Кнопка **«Отчёт об ошибке»** в окне мультиплеера сохраняет файл с версиями, списком модов и логом (путь копируется) —
приложите его к [issue](https://github.com/Vanket/RimMult/issues) или комментарию в Мастерской. Или `Player.log`:
`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`, строки `[RimMult]`.

## Разработка

Нужен .NET SDK 10.

```sh
dotnet build -c Release      # мод соберётся в mod/1.6/Assemblies
dotnet test -c Release
```

| Путь | Что это |
|---|---|
| `src/RimMult.Shared` | Протокол, пакеты, сериализация, общее время, мир, летопись (`netstandard2.0`) |
| `src/RimMult.ServerCore` | Сервер — общий для хоста в игре и выделенного сервера (`netstandard2.0`) |
| `src/RimMult.ClientCore` | Клиентская сессия, торговля (`netstandard2.0`) |
| `src/RimMult.Net.LiteNet` | UDP-транспорт: вход по адресу и выделенный сервер |
| `src/RimMult.Server` | Выделенный сервер (`.NET 10`, Docker) |
| `src/RimMult.Mod` | Сам мод (`net472`, Harmony) |
| `mod/` | Папка мода для RimWorld (`About/`, `Defs/`, `Languages/`; сборки — в `1.6/Assemblies/`) |
| `tests/RimMult.Tests` | Юнит-тесты |
| `tools/workshop` | Публикация в Мастерскую одной командой ([`docs/WORKSHOP.md`](docs/WORKSHOP.md)) |

Документация: [`docs/HANDOFF.md`](docs/HANDOFF.md) — состояние проекта и с чего начать ·
[`docs/DESIGN.md`](docs/DESIGN.md) — архитектура · [`docs/TESTING.md`](docs/TESTING.md) — как проверять в игре.

---

<a id="english"></a>

# RimMult — multiplayer for RimWorld 1.6

2–10 players over Steam. No lockstep: **every PC simulates only its own game** and sends the others what changed.
Mods don't cause desyncs, and a weak PC isn't slowed down by other players' colonies.

> **Early version.** Connecting, the shared world, parcels, trade and co-op are verified in game; raids, helping
> allies, the chronicle and parts of co-op are written and being checked.

**Separate colonies** — everyone founds their own colony on one shared planet, one calendar, speed by vote:
parcels (even to offline players), moving colonists over, trade, diplomacy (alliance / war), raids led by the AI or
**in person** (you load the defender's game and command your fighters), helping an ally in person, a shared NPC
world, and a **world chronicle** with players' stats — kept by the host's save or the dedicated server.

**Co-op** — everyone plays the host's colony live: any orders (designations, building, drafting, right-click with
Shift queue, targeted attacks and abilities, priorities, research, quests), bills, storage, schedules, policies,
caravans on the globe, friends' cursors and selection in their color, mods' data for guests.

**Install:** subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3813029532)
(needs Harmony). Everyone needs the same RimMult version and the same mods in the same order — "Mods" next to a friend
compares before joining, and one click makes yours like the host's (Workshop downloads included); visual/UI mods can be marked "client-side" in its settings. Not compatible with Multiplayer (Zetrith).

**Play:** main menu → **Multiplayer** → host and pick a mode; friends join from that window or the Steam friends list.
Chat: **`\`**. Dedicated server for separate colonies (Linux, Windows, Docker): [`docs/SERVER.md`](docs/SERVER.md).

**Bugs:** the **"Bug report"** button in the multiplayer window saves a file with versions, mods and the log — attach
it to an [issue](https://github.com/Vanket/RimMult/issues).

**Build:** .NET SDK 10, `dotnet build -c Release`, `dotnet test -c Release`. Start with
[`docs/HANDOFF.md`](docs/HANDOFF.md) (project state, in Russian).
