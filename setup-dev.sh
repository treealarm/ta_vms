#!/bin/bash
set -e

echo "=== 1. .NET 8 SDK ==="
if dotnet --version 2>/dev/null | grep -q "^8\."; then
    echo "Already installed: $(dotnet --version)"
else
    wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    sudo dpkg -i /tmp/packages-microsoft-prod.deb
    rm /tmp/packages-microsoft-prod.deb
    sudo apt-get update -q
    sudo apt-get install -y dotnet-sdk-8.0
fi

echo "=== 2. Node.js 20 LTS ==="
if node --version 2>/dev/null | grep -q "^v20\."; then
    echo "Already installed: $(node --version)"
else
    sudo apt-get remove -y libnode-dev libnode72 nodejs 2>/dev/null || true
    curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
    sudo apt-get install -y nodejs
fi

echo "=== 3. GCC 13 (for std::format support) ==="
if g++-13 --version 2>/dev/null | grep -q "13\."; then
    echo "Already installed: $(g++-13 --version | head -1)"
else
    sudo apt-get install -y gcc-13 g++-13
fi
sudo update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-13 13 --slave /usr/bin/g++ g++ /usr/bin/g++-13
sudo update-alternatives --set gcc /usr/bin/gcc-13

echo "=== 4. Dapr CLI ==="
if dapr --version 2>/dev/null | grep -q "Runtime version"; then
    echo "Already installed: $(dapr --version)"
else
    wget -q -O /tmp/install-dapr.sh https://raw.githubusercontent.com/dapr/cli/master/install/install.sh
    sudo bash /tmp/install-dapr.sh
    rm /tmp/install-dapr.sh
    dapr init
fi

echo ""
echo "=== Done ==="
dotnet --version
node --version
npm --version
gcc --version | head -1
g++ --version | head -1
dapr --version
