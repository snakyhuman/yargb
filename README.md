# yargb

Компактная Windows-утилита и CLI для двух разъёмов JARGB_V2 на MSI B760 GAMING PLUS WIFI (MS-7D98). В окне доступны четыре редактируемых цветовых пресета, яркость от 10 до 100%, выключатель подсветки и Game Sync.

Подсветка управляется через USB HID. Игровые эффекты обрабатывает установленная фоновая служба MSI Mystic Light. Открывать окно MSI Center для Game Sync не требуется.

## Сборка

Нужен .NET 10 SDK на Windows x64. Команды запускаются из корня репозитория.

```powershell
dotnet publish Jargb.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=none -o bin/current
dotnet publish Cli/Jargb.Cli.csproj -c Release
```

Окно запускается из `bin/current/Jargb.exe`, консольная утилита — из `bin/cli/jargb-cli.exe`. Для запуска окна нужен .NET 10 Desktop Runtime, для CLI — .NET 10 Runtime.

## CLI

```powershell
& .\bin\cli\jargb-cli.exe status
& .\bin\cli\jargb-cli.exe preset 2
& .\bin\cli\jargb-cli.exe color 53BFFF
& .\bin\cli\jargb-cli.exe brightness 40
& .\bin\cli\jargb-cli.exe game-sync on
& .\bin\cli\jargb-cli.exe off
```

`off` выключает подсветку и Game Sync. `on` возвращает последний цвет. Полный список команд доступен через `--help`.
