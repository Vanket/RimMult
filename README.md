# RimMult

Мультиплеер для **RimWorld 1.6**: у каждого игрока своя колония (или несколько) в одном общем мире,
можно ходить друг к другу в гости, торговать и воевать. Рассчитан на 4–10 игроков и работу с модами.

Главное отличие от lockstep-мультиплеера: **каждый компьютер симулирует только свои карты**, поэтому слабый
ПК не тормозит из-за чужих колоний. Если он не тянет даже свою, её симуляцию можно передать на мощный ПК.

Подробная архитектура и план: [docs/DESIGN.md](docs/DESIGN.md).

> Статус: ранняя разработка (этап 0 — скелет, протокол, общий таймер с голосованием). Играть пока нельзя.

## Структура

| Путь | Что это |
|---|---|
| `src/RimMult.Shared` | Протокол, пакеты, сериализация, координатор общего времени (`netstandard2.0`) |
| `src/RimMult.ServerCore` | Логика сервера, общая для хоста в игре и выделенного сервера (`netstandard2.0`) |
| `src/RimMult.Server` | Выделенный сервер (`.NET 10`, UDP/LiteNetLib, Docker) |
| `src/RimMult.Mod` | Сам мод (`net472`, Harmony) |
| `mod/` | Папка мода для RimWorld: `About/`, сборки попадают в `1.6/Assemblies/` |
| `tests/RimMult.Tests` | Юнит-тесты |

## Сборка

Нужен .NET SDK 10.

```sh
dotnet build -c Release     # мод соберётся прямо в mod/1.6/Assemblies
dotnet test -c Release
```

Чтобы проверить в игре, сделай ссылку на папку `mod` в `RimWorld/Mods`:

```sh
# Windows (cmd от администратора)
mklink /D "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimMult" "<путь к репо>\mod"
```

Нужен мод [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077).

## Выделенный сервер

```sh
dotnet run -c Release --project src/RimMult.Server            # создаст server.json с настройками по умолчанию
# или в Docker
docker build -f src/RimMult.Server/Dockerfile -t rimmult-server .
docker run -d -p 26480:26480/udp -v rimmult-data:/data rimmult-server
```

Пример конфига с комментариями: [`src/RimMult.Server/server.example.json`](src/RimMult.Server/server.example.json).
