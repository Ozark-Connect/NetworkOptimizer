VERSION ?= dev

.PHONY: build build-gateway build-local clean

# Build for the UniFi gateway (linux/arm64, static binary)
build-gateway:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/cfspeedtest-linux-arm64 .

# Build for local machine (for testing)
build-local:
	go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/cfspeedtest .

# Build both targets
build: build-gateway build-local

clean:
	rm -rf bin/
