VERSION ?= dev

.PHONY: build build-ap build-ap-arm build-ap-mips build-ap-mipsle build-local test vet clean

# Build static binaries for the supported AP userspaces. MIPS gets both byte orders and uses
# soft-float so the binary does not depend on an FPU or a particular floating-point ABI.
build-ap: build-ap-arm build-ap-mips build-ap-mipsle

build-ap-arm: | bin
	CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-arm .

build-ap-mips: | bin
	CGO_ENABLED=0 GOOS=linux GOARCH=mips GOMIPS=softfloat go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-mips .

build-ap-mipsle: | bin
	CGO_ENABLED=0 GOOS=linux GOARCH=mipsle GOMIPS=softfloat go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent-linux-mipsle .

# Build for local machine (for testing)
build-local: | bin
	go build -trimpath \
		-ldflags "-s -w -X main.version=$(VERSION)" \
		-o bin/apagent .

# Build both targets
build: build-ap build-local

test:
	go test ./...

vet:
	go vet ./...

bin:
	mkdir -p bin

clean:
	rm -rf bin/
