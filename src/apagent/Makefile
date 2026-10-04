VERSION ?= dev

.PHONY: build build-ap build-local test vet clean

# Build for UniFi APs (static binaries). U7-class APs are armv7l, where an arm64 build will not
# exec; arm64 is for UniFi OS gateways with Wi-Fi (UDR7, UX7, UCG-Industrial). MIPS is built in both
# byte orders (little-endian MediaTek U6-Lite, big-endian QCA UAP-AC), soft-float because neither
# has an FPU.
build-ap:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-arm .
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-arm64 .
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
