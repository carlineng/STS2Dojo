DOTNET ?= /usr/local/share/dotnet/dotnet
PROJECT := STS2Dojo.csproj
TEST_PROJECT := STS2Dojo.Tests/STS2Dojo.Tests.csproj
BUILD_MODS_PATH ?= /private/tmp/sts2dojo-mods/

.PHONY: build deploy test clean

build:
	$(DOTNET) build $(PROJECT) -p:ModsPath="$(BUILD_MODS_PATH)"

deploy:
	$(DOTNET) build $(PROJECT)

test:
	$(DOTNET) run --project $(TEST_PROJECT)

clean:
	$(DOTNET) clean $(PROJECT)
