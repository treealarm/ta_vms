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

# Запустить JPEG-стрим (через gRPC stream)
dotnet run --project tools/vms-control-client -- start-jpeg cam-1

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
start-jpeg cam-1
stop-jpeg cam-1
stop cam-1
exit
```

## Приём JPEG через protobuf/gRPC

Когда в клиенте вызывается `start-jpeg <camera-id>`, клиент:

1. вызывает `StartJpegStream` на сервере;
2. открывает gRPC server-streaming вызов `StreamJpegFrames`;
3. сохраняет полученные `JpegFrame` сообщения в папку `jpeg_frames` рядом с бинарником.

Получение останавливается при `stop-jpeg <camera-id>`.

По умолчанию клиент подключается к `localhost:50051`. Адрес можно переопределить:

```bash
dotnet run --project tools/vms-control-client -- --address 192.168.1.10:50051 start cam-1
```
