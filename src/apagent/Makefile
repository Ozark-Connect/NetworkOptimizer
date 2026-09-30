VERSION ?= dev

.PHONY: build build-ap build-local test vet clean

# Build for UniFi APs (static binaries). Every measured U7-class AP is armv7l; an arm64 build
# will not exec on them, so there is deliberately no arm64 target. MIPS is built in both byte
# orders (little-endian MediaTek U6-Lite, big-endian QCA UAP-AC), soft-float because neither has
# an FPU.
build-ap:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-arm .
	CGO_ENABLED=0 GOOS=linux GOARCH=mipsle GOMIPS=softfloat go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-mipsle .
	CGO_ENABLED=0 GOOS=linux GOARCH=mips GOMIPS=softfloat go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-mips .

# Build for local machine (for testing)
build-local:
	go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent .

# Build both targets
build: build-ap build-local

test:
	go test ./...

vet:
	go vet ./...

clean:
	rm -rf bin/
