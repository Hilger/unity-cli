# Build native_server plugin for Unity CLI Connector
# Usage:
#   make macos     — universal binary (arm64 + x86_64)
#   make windows   — cross-compile via mingw-w64
#   make linux     — native Linux build (run on Linux or in Docker)
#   make all       — macos + windows (Linux requires native toolchain)
#   make clean

SRC = native_server.c
CFLAGS = -shared -fPIC -O2 -Wall -Wextra -Wno-unused-parameter

# macOS: universal .dylib inside a .bundle
BUNDLE = ../unity-connector/Plugins/native_server.bundle
DYLIB_ARM64 = build/native_server_arm64.dylib
DYLIB_X86   = build/native_server_x86_64.dylib
DYLIB_UNI   = $(BUNDLE)/Contents/MacOS/native_server

# Windows: .dll via mingw-w64
MINGW = x86_64-w64-mingw32-gcc
DLL = native_server.dll

# Linux: .so
SO = libnative_server.so

.PHONY: all macos windows linux clean

all: macos windows

macos: $(DYLIB_UNI)

$(DYLIB_UNI): $(SRC)
	@mkdir -p build
	@mkdir -p $(BUNDLE)/Contents/MacOS
	cc $(CFLAGS) -arch arm64 -o $(DYLIB_ARM64) $(SRC)
	cc $(CFLAGS) -arch x86_64 -o $(DYLIB_X86) $(SRC)
	lipo -create $(DYLIB_ARM64) $(DYLIB_X86) -output $(DYLIB_UNI)
	@cp Info.plist $(BUNDLE)/Contents/Info.plist
	@echo "Built: $(BUNDLE)"

windows: $(DLL)

$(DLL): $(SRC)
	$(MINGW) $(CFLAGS) -o $(DLL) $(SRC) -lws2_32
	@echo "Built: $(DLL)"

linux: $(SO)

$(SO): $(SRC)
	gcc $(CFLAGS) -o $(SO) $(SRC)
	@echo "Built: $(SO)"

clean:
	rm -rf build $(BUNDLE) $(DLL) $(SO)
