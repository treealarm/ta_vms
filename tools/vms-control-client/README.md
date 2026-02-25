# VMS gRPC test control client (.NET 8)

Небольшой CLI-клиент для ручного управления `CameraControlService`.

## Режимы работы

### 1) Одноразовая команда

```bash
# Добавить камеру и сразу запустить запись
dotnet run --project tools/vms-control-client -- add cam-1 rtsp://127.0.0.1:8554/live --start

# Запустить запись
dotnet run --project tools/vms-control-client -- start cam-1

# Остановить запись
dotnet run --project tools/vms-control-client -- stop cam-1

# Запустить JPEG-стрим
dotnet run --project tools/vms-control-client -- start-jpeg cam-1 127.0.0.1 9000

# Остановить JPEG-стрим
dotnet run --project tools/vms-control-client -- stop-jpeg cam-1
```

### 2) Интерактивный режим (несколько команд подряд)

```bash
dotnet run --project tools/vms-control-client -- --address localhost:50051
```

После запуска можно вводить команды без перезапуска клиента:

```text
add cam-1 rtsp://127.0.0.1:8554/live --start
start-jpeg cam-1 127.0.0.1 9000
stop-jpeg cam-1
stop cam-1
exit
```

## Встроенный приёмник JPEG

Когда в клиенте вызывается `start-jpeg <camera-id> <host> <port>`, клиент автоматически поднимает TCP listener на указанном `port` и принимает JPEG-кадры в формате:

- 4 байта длины кадра (big-endian)
- байты JPEG кадра

Полученные кадры сохраняются в папку `jpeg_frames` рядом с бинарником клиента.

> Важно: `host` и `port` в команде `start-jpeg` должны указывать на машину, где запущен этот клиент (иначе сервер будет стримить в другое место).

По умолчанию клиент подключается к `localhost:50051`. Адрес можно переопределить:

```bash
dotnet run --project tools/vms-control-client -- --address 192.168.1.10:50051 start cam-1
```
