# ProxMonitor

Десктоп-приложение для мониторинга и управления серверами Proxmox VE: живая таблица нод, VM и контейнеров, предупреждения, виджеты и терминал. C# / .NET 10 / Avalonia.

## Запуск

```
dotnet run --project ProxMonitor.App
```

Без сервера: кнопка **«Открыть демо»** или `dotnet run --project ProxMonitor.App -- --demo`.

## Подключение к Proxmox

1. В веб-интерфейсе Proxmox: **Datacenter → Permissions → API Tokens → Add**. Для просмотра хватит роли `PVEAuditor`, для запуска и остановки нужна `PVEVMUser`. Скопируйте секрет: он показывается один раз.
2. В приложении: **«Подключить сервер»** → адрес `https://<IP>:8006`, Token ID `user@realm!имя`, секрет.
3. Сверьте отпечаток сертификата с **Нода → System → Certificates** и подтвердите.

Терминал (**Свойства → Терминал**) работает по SSH к ноде.

## Тесты

```
dotnet test
```
