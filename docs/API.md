# RimMult API для модов

Большинству модов делать ничего не нужно. В RimMult нет lockstep, поэтому рассинхронов не бывает. Вещи, пешки, компоненты вещей (`ThingComp`) и компоненты игры, мира и карт переходят между играми через обычное сохранение RimWorld. В коопе компоненты модов ещё и сами передаются гостям при изменении (галочка «Кооп: передавать гостям данные модов» в настройках RimMult).

API нужен, если:
* кнопка мода меняет состояние, которое хранится вне вещей и компонентов (статическое поле, кеш);
* кнопка мода открывает своё окно, и у гостя коопа результат этого окна должен дойти до хоста.

## Подключение

Без жёсткой зависимости — через рефлексию:

```csharp
var api = AccessTools.TypeByName("RimMult.API.RimMultAPI");
if (api != null)
{
    AccessTools.Method(api, "RegisterCommand").Invoke(null, new object[] { "me.mymod:toggle", (Action<byte[]>)OnToggle });
}
```

Или сошлитесь на `RimMult.dll` (с `loadAfter` vanket.rimmult) и пишите напрямую:

```csharp
using RimMult.API;

[StaticConstructorOnStartup]
static class MyModMultiplayer
{
    static MyModMultiplayer()
    {
        // На всех машинах: что делать, когда команда дошла до игры, которая симулирует.
        RimMultAPI.RegisterCommand("me.mymod:toggle", data => MyComp.Toggle(BitConverter.ToInt32(data, 0)));

        // Состояние вне вещей и компонентов: хост раз в секунду сохраняет его, гости получают при изменении.
        RimMultAPI.RegisterState("me.mymod:mode", () => MyMod.Mode.ToString(), text => MyMod.Mode = int.Parse(text));

        // Эта кнопка только открывает окно мода: у гостя она работает в его копии.
        RimMultAPI.RegisterLocalGizmo(typeof(Command_OpenMyWindow));
    }
}

// В кнопке, вместо прямого изменения:
action = () => RimMultAPI.SendCommand("me.mymod:toggle", BitConverter.GetBytes(parent.thingIDNumber));
```

## Справка

| Член | Что делает |
|---|---|
| `Version` | Версия API (сейчас 1). |
| `IsMultiplayer` | Подключён к серверу RimMult. |
| `IsCoop`, `IsCoopHost` | Сервер в режиме коопа; эта игра — его хост. |
| `IsShowingCopy` | Эта игра показывает копию чужой (гость коопа, «рейд лично», «помощь лично»): она не тикает, изменения в ней пропадут, если не отправить их через `SendCommand`. |
| `IsSimulating` | Эта игра действительно симулирует (всё, кроме копии). |
| `PlayerName`, `PlayerNames` | Имя этого игрока и всех на сервере. |
| `RegisterCommand(id, onHost)` | Команда мода. Регистрируйте на всех машинах. |
| `SendCommand(id, data)` | У гостя коопа отправляет команду хосту, в остальных случаях выполняет сразу. |
| `RegisterState(key, save, load)` | Состояние мода, которое должны видеть гости коопа. |
| `RegisterLocalGizmo(type)` | Кнопки этого типа у гостя выполняются в его копии (окна мода). |
| `ExcludeComponent(type)` | Не передавать этот компонент гостям (слишком большой или пересчитывается на месте). |

Все вызовы — только из главного потока игры.
