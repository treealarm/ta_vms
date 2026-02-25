# VMS gRPC test control client (.NET 8)

Небольшой CLI-клиент для ручного управления `CameraControlService`.

## Команды

```bash
# Добавить камеру и сразу запустить запись
dotnet run --project tools/vms-control-client -- add cam-1 rtsp://127.0.0.1:8554/live --start

# Запустить запись
dotnet run --project tools/vms-control-client -- start cam-1

# Остановить запись
dotnet run --project tools/vms-control-client -- stop cam-1
```

По умолчанию клиент подключается к `localhost:50051`. Адрес можно переопределить:

```bash
dotnet run --project tools/vms-control-client -- --address 192.168.1.10:50051 start cam-1
```
