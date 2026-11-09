# Аппаратное кодирование на Windows без карты NVIDIA

Найдено 2026-10-26 при переносе NVENC на Windows с CUDA на Direct3D 11 (roi_transcode,
`Engine::Nvenc` и `DecodeEngine::D3d11va`). Половина декодирования у нейтрального к вендору
Windows-пути уже есть: D3D11VA, в Windows-сборке FFmpeg (vcpkg собирает её с `--enable-d3d11va`),
на GPU любого вендора. Без аппаратного кодировщика каждый HEVC-поток уходит в
`video_transcode_processor`, включая декодирование — 121–135 % ядра на поток 1080p25 на i5-750
(`../ta_install/windows/FINDINGS.md`).

**QSV через D3D11, для Intel, уже есть** (2026-10-26): `ffmpeg[qsv]` в Windows-портах vcpkg,
QSV-бэкенд на D3D11-устройстве на адаптере Intel с переданным ему декодером, и `IntelOneVpl` после
`Nvenc` в порядке по умолчанию в `video_srv` (`hw_transcode_processor.cpp`,
`encoder_choice_from_env`). Что осталось:

- **Проверка на Windows.** Тот же код проверен на Linux, где QSV работает через VA-API: на UHD 770
  i7-12700 сессия `Transcoder` декодирует на GPU, ничего не загружает и ставит каждую картинку на
  место (`transcode_device_test` в roi_transcode). На Windows отличается только тип устройства —
  D3D11 на адаптере с vendor id `0x8086`.

  **Проверено к настоящему моменту** (2026-10-26, i7-12700 / UHD 770, Windows 11 26200, драйвер
  Intel 30.0.101.1371 от февраля 2022 — намеренно не обновлялся), на ассете `windows-build` из
  `d596529`:
  - `vms.exe` запускается, как запускает CI: все DLL загружаются, в логе порядок
    `vaapi -> nvenc -> qsv`, и процесс останавливается на `DAPR_GRPC_PORT`.
  - `h264_qsv` через поставляемый FFmpeg (9.0.1) и `libvpl.dll`, скриптом
    `scripts/win/qsv-probe.py`: устройства D3D11VA и QSV открываются на `8086:4680`, runtime
    oneVPL драйвера сообщает реализацию 2.6, а кодировщик — *"Initialized an internal MFX session
    using hardware accelerated implementation"*. 300 кадров 1080p NV12 из системной памяти,
    H.264 High на 4 Мбит/с: 300 пакетов за 1.5 с, около 200 fps. Драйвера 2022 года достаточно;
    для QSV на Alder Lake обновлять ничего не нужно.

  **Сам vms, проверено** (2026-10-27, та же машина, установщик ta_install `229935a`, без
  `VMS_VIDEO_ENCODER`, 1080p25 HEVC Main): `decoding on d3d11va, encoding on 'h264_qsv (Intel VPL
  mfxExtEncoderROI)'`, 9.5 % ядра на просматриваемый поток против 74 % на
  `video_transcode_processor`. Подробности — `../ta_install/windows/FINDINGS.md`.

  **Ivy Bridge, проверено** (2026-10-27, 192.168.1.82, HD 2500, кандидат v0.1.8, 1080p25 HEVC):
  QSV сразу отказывается, `no QSV runtime on the Intel d3d11va device`, и поток уходит в
  `video_transcode_processor` на 88 % ядра, как на v0.1.7 (89 %). Его Quick Sync кодирует H.264
  только через legacy Media SDK runtime, и диспетчер не нашёл там runtime, который стал бы
  загружать; ничего не ломается и ничего не выигрывается. Почему именно (runtime драйвера или
  представление диспетчера о нём) — не разбирались: не стоит того для iGPU 2012 года, если у
  заказчика такого нет.

  **Несколько потоков, проверено** (2026-10-28, та же машина, ta_vms `7da89af`): восемь камер на
  одном источнике 1080p25 HEVC, каждый push поддерживается RTSP-читателем. Все восемь открыли
  `h264_qsv` на D3D11VA, ни один не отказался и не откатился; vms на 25 % ядра с четырьмя, 38 % с
  восемью, видеодвижок GPU на 28 % и 49 % — около 5 % ядра и 6 % движка на поток, так что конец
  положит iGPU, а не лимит сессий, где-то около шестнадцати. Больше восьми не пробовали.

  **Осталось:** `transcode_device_test` на Windows не запускался. 192.168.1.100 — i5-750 без
  Quick Sync.
- **HEVC на QSV** на той машине открылся и ничего не закодировал на Linux (`qsv: opened but
  encodes nothing`, так было и до изменения). vms кодирует только H.264, так что на его пути это
  не стоит.

Другие вендоры:

- **AMF, для AMD.** `h264_amf` принимает кадры D3D11, как NVENC; в ffmpeg vcpkg есть фича `amf`.
  AMD-железа для проверки нет вовсе.
- **Media Foundation (`h264_mf`) как универсальный.** Уже в Windows-сборке FFmpeg
  (`--enable-mediafoundation`); он доходит до кодировщика MFT любого установленного вендора через
  один API. Рассмотрен и пока отложен: его задержка и rate control на живом видео не измерены, и
  сначала нужно сравнение с NVENC на машине NVIDIA.

Каждый из них — бэкенд в roi_transcode и запись после `IntelOneVpl` в порядке по умолчанию в
`video_srv` (`hw_transcode_processor.cpp`, `encoder_choice_from_env`), и `is_hardware` там должен
его узнать.
