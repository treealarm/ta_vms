FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive


# Добавляем PPA для GCC 13
RUN apt-get update && apt-get install -y software-properties-common \
    && add-apt-repository ppa:ubuntu-toolchain-r/test -y \
    && apt-get update

# Установка GCC 13, CMake и других зависимостей
RUN apt-get install -y \
    build-essential \
    gcc-13 g++-13 \
    cmake \
    ninja-build \
    pkg-config \
    curl zip unzip tar git \
    protobuf-compiler \
    libprotobuf-dev \
    libgrpc++-dev \
    libavformat-dev \
    libavcodec-dev \
    libavutil-dev \
    libswscale-dev \
    && update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-13 100 \
    && update-alternatives --install /usr/bin/g++ g++ /usr/bin/g++-13 100 \
    && rm -rf /var/lib/apt/lists/*

# Делаем GCC 13 и G++ 13 дефолтными
RUN update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-13 100 \
    && update-alternatives --install /usr/bin/g++ g++ /usr/bin/g++-13 100
	
# устанавливаем необходимые инструменты для vcpkg и сборки
RUN apt-get update && apt-get install -y \
    curl zip unzip tar git build-essential cmake ninja-build pkg-config \
    protobuf-compiler libprotobuf-dev libgrpc++-dev \
    libavformat-dev libavcodec-dev libavutil-dev libswscale-dev \
    && rm -rf /var/lib/apt/lists/*

# клонируем и ставим vcpkg
RUN git clone https://github.com/microsoft/vcpkg.git /vcpkg \
    && /vcpkg/bootstrap-vcpkg.sh \
    && /vcpkg/vcpkg install protobuf grpc
	

ENV CMAKE_TOOLCHAIN_FILE=/vcpkg/scripts/buildsystems/vcpkg.cmake

# 2. Рабочая директория
WORKDIR /app

# 3. Копируем проект
COPY . .

# 4. Сборка
WORKDIR /app/media_server
RUN cmake -B build -G Ninja
RUN cmake --build build -j

# 5. Запуск
CMD ["./build/vms"]